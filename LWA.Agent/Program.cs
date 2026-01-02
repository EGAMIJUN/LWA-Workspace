using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Linq;
using System;
using System.Threading;
using System.Threading.Tasks; // 追加: 非同期処理用
using System.Collections.Generic;
using System.Text.Json; // 追加: JSON処理用
using FlaUI.UIA3;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
// 追加: AWSライブラリ
using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;

// 競合回避
using TextBox = FlaUI.Core.AutomationElements.TextBox;
using Button = FlaUI.Core.AutomationElements.Button;
using ComboBox = FlaUI.Core.AutomationElements.ComboBox;
using CheckBox = FlaUI.Core.AutomationElements.CheckBox;

// ==========================================
// 0. AWS設定 (ここに自分の鍵を入れる！)
// ==========================================
// ★重要: GitHubに上げる時はここを消すか、環境変数を使うこと！
// 環境変数から読み込む（無ければ空文字）
static readonly string AwsAccessKey = Environment.GetEnvironmentVariable("LWA_ACCESS_KEY") ?? "";
static readonly string AwsSecretKey = Environment.GetEnvironmentVariable("LWA_SECRET_KEY") ?? "";
// const ではなく static readonly にすること！
const string SqsUrl = "https://sqs.ap-northeast-1.amazonaws.com/038751768591/LwaCommandQueue"; // さっきのURL
const string Region = "ap-northeast-1"; // 東京リージョン

// ==========================================
// 1. Webサーバー設定
// ==========================================
var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();
app.UseCors("AllowAll");

app.MapGet("/", () => "LWA Agent Phase 3 (AWS Connected) 🚀");

// 手動実行用API
app.MapPost("/run", (JobRequest req) => 
{
    Console.WriteLine($"[Web] No:{req.ContainerNo}, Type:{req.Type}");
    try 
    {
        string result = RunRobot(req);
        return Results.Ok(new { message = "Success", detail = result });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// 在庫確認API
app.MapGet("/inventory", () =>
{
    Console.WriteLine("[Web] 在庫一覧取得リクエスト");
    try
    {
        var items = GetInventory();
        return Results.Ok(items);
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message);
    }
});

// ★★★ ここが新機能: SQSポーリング開始 ★★★
// Webサーバーとは別の「裏スレッド」でSQS監視をスタートさせる
var sqsTask = Task.Run(() => StartSqsPolling());

Console.WriteLine("=== LWA Agent Server Started (http://localhost:5000) ===");
Console.WriteLine($"=== SQS Polling Started: {SqsUrl} ===");

app.Run("http://localhost:5000");

// ==========================================
// 2. SQS監視ロジック (Worker)
// ==========================================
static async Task StartSqsPolling()
{
    // ここで落ちるならRegionがおかしい。const string Regionを確認せよ。
    var sqsConfig = new AmazonSQSConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(Region) };
    var sqsClient = new AmazonSQSClient(AwsAccessKey, AwsSecretKey, sqsConfig);

    Console.WriteLine("[AWS] 接続準備OK。命令を待機中...");

    while (true)
    {
        try
        {
            var request = new ReceiveMessageRequest
            {
                QueueUrl = SqsUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 20
            };

            // ここで落ちるなら sqsClient が null (ありえない)
            var response = await sqsClient.ReceiveMessageAsync(request);

            // ★★★ 修正箇所: nullチェックを追加 ★★★
            // response自体がnull、またはMessagesがnullの場合は無視する
            if (response != null && response.Messages != null && response.Messages.Count > 0)
            {
                var msg = response.Messages[0];
                Console.WriteLine($"\n[AWS] 受信: {msg.Body}");

                try 
                {
                    var job = JsonSerializer.Deserialize<JobRequest>(msg.Body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (job != null)
                    {
                        Console.WriteLine($"[AWS] ロボット起動: {job.ContainerNo}");
                        
                        // メインスレッドのUI操作が必要な場合があるが、FlaUIは比較的寛容
                        string result = RunRobot(job); 
                        
                        Console.WriteLine($"[AWS] 実行完了: {result}");

                        await sqsClient.DeleteMessageAsync(SqsUrl, msg.ReceiptHandle);
                        Console.WriteLine("[AWS] キューから削除完了");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[AWS Error] 処理失敗: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SQS Error] 通信エラー: {ex.Message}");
            Console.WriteLine($"[場所] {ex.StackTrace}");
            await Task.Delay(5000); 
        }
    }
}
// ==========================================
// 3. ロボット制御ロジック (既存流用)
// ==========================================
static string RunRobot(JobRequest req)
{
    Console.WriteLine("DEBUG: 1. プロセス検索開始");
    var process = Process.GetProcessesByName("MiniPortLegacy").FirstOrDefault();
    if (process == null) throw new Exception("MiniPortLegacyが起動していません。");

    Console.WriteLine("DEBUG: 2. ウィンドウ接続");
    using var automation = new UIA3Automation();
    var app = FlaUI.Core.Application.Attach(process);
    var window = app.GetMainWindow(automation);
    if (window == null) throw new Exception("画面が見つかりません。");
    window.Focus();

    Console.WriteLine("DEBUG: 3. タブ切り替え");
    var tab = RetryFind(window, "TabInput");
    if (tab != null)
    {
        if (tab.Patterns.SelectionItem.IsSupported) tab.Patterns.SelectionItem.Pattern.Select();
        else tab.Click();
        Thread.Sleep(500);
    }

    Console.WriteLine("DEBUG: 4. コンテナNo入力");
    // ★修正: タイムアウトを 5000ms (5秒) に延長！
    var elNo = RetryFind(window, "txtContainerNo", 5000); 
    
    if (elNo == null) throw new Exception("コンテナNo欄が見つかりません (5秒待ちましたがダメでした)");
    new TextBox(elNo.FrameworkAutomationElement).Text = req.ContainerNo;

    Console.WriteLine("DEBUG: 5. コンボボックス検索");
    var elType = RetryFind(window, "cmbContainerType");
    if (elType == null) throw new Exception("タイプ選択欄が見つかりません");
    
    // キーボード入力戦略 (最強版)
    Console.WriteLine($"DEBUG: 6. コンボボックス操作 (Keyboard): {req.Type}");
    elType.Focus();
    Thread.Sleep(300);
    FlaUI.Core.Input.Keyboard.Type(req.Type);
    Thread.Sleep(500);
    FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
    Console.WriteLine($"  -> キーボードで '{req.Type}' を打ち込みました");

    Console.WriteLine("DEBUG: 7. チェックボックス操作");
    var elChk = RetryFind(window, "chkDamaged");
    if (elChk != null)
    {
        var chk = new CheckBox(elChk.FrameworkAutomationElement);
        chk.IsChecked = req.IsDamaged;
    }

    Console.WriteLine("DEBUG: 8. ボタン押下");
    var elBtn = RetryFind(window, "btnRegister");
    if (elBtn == null) throw new Exception("登録ボタンが見つかりません");
    
    new Button(elBtn.FrameworkAutomationElement).Invoke();

    return $"登録完了: {req.ContainerNo}";
}

static List<InventoryItem> GetInventory()
{
    Console.WriteLine("DEBUG: 1. プロセス検索開始 (Read)");
    var process = Process.GetProcessesByName("MiniPortLegacy").FirstOrDefault();
    if (process == null) throw new Exception("MiniPortLegacyが起動していません。");

    using var automation = new UIA3Automation();
    var app = FlaUI.Core.Application.Attach(process);
    var window = app.GetMainWindow(automation);
    if (window == null) throw new Exception("画面が見つかりません。");
    window.Focus();

    Console.WriteLine("DEBUG: 2. 在庫一覧タブに切り替え");
    var tab = window.FindFirstDescendant(cf => cf.ByAutomationId("TabList"));
    if (tab == null) tab = window.FindFirstDescendant(cf => cf.ByName("在庫一覧"));

    if (tab != null)
    {
        if (tab.Patterns.SelectionItem.IsSupported) tab.Patterns.SelectionItem.Pattern.Select();
        else tab.Click();
        Console.WriteLine("  -> タブをクリックしました。描画を待ちます...");
        Thread.Sleep(1000);
    }
    else
    {
        throw new Exception("在庫一覧タブが見つかりません");
    }

    Console.WriteLine("DEBUG: 3. グリッド検索");
    var elGrid = RetryFind(window, "gridInventory", 3000);
    if (elGrid == null)
    {
        Console.WriteLine("  -> IDで見つからないため、コントロールタイプ(Table)で検索します...");
        elGrid = window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Table));
    }
    if (elGrid == null) throw new Exception("グリッドが見つかりません");

    var items = new List<InventoryItem>();
    Console.WriteLine("DEBUG: 4. 行データの読み取り");
    var rows = elGrid.FindAllChildren();

    foreach (var row in rows)
    {
        var cells = row.FindAllChildren();
        if (cells.Length >= 4)
        {
            string GetVal(AutomationElement cell)
            {
                if (cell.Patterns.Value.IsSupported) return cell.Patterns.Value.Pattern.Value;
                if (cell.Patterns.LegacyIAccessible.IsSupported) return cell.Patterns.LegacyIAccessible.Pattern.Value;
                return cell.Name;
            }

            var values = cells.Select(c => GetVal(c)).ToList();

            if (values.Count >= 5) 
            {
                 string no = values[1];
                 string type = values[2];
                 string damaged = values[3];
                 string time = values[4];

                 if (no == "No") continue;
                 if (no == "(なし)") continue;
                 if (string.IsNullOrWhiteSpace(no)) continue;
                 if (no.Contains("列") || no.Contains("ヘッダー")) continue;

                 items.Add(new InventoryItem(no, type, damaged, time));
                 Console.WriteLine($"  -> 読み取り成功: {no}, {type}");
            }
            else if (values.Count == 4)
            {
                 string no = values[0];
                 string type = values[1];
                 string damaged = values[2];
                 string time = values[3];
                 
                 if (no.Contains("列") || no.Contains("ヘッダー")) continue;
                 items.Add(new InventoryItem(no, type, damaged, time));
            }
        }
    }

    Console.WriteLine($"DEBUG: 5. 完了（{items.Count}件取得）");
    return items;
}

static AutomationElement? RetryFind(AutomationElement root, string automationId, int timeoutMs = 2000)
{
    var stopwatch = Stopwatch.StartNew();
    while (stopwatch.ElapsedMilliseconds < timeoutMs)
    {
        var element = root.FindFirstDescendant(cf => cf.ByAutomationId(automationId));
        if (element != null) return element;
        Thread.Sleep(100);
    }
    return null;
}

public record JobRequest(string ContainerNo, string Type, bool IsDamaged);
public record InventoryItem(string No, string Type, string Damaged, string Time);