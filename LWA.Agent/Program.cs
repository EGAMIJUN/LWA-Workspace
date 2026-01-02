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
using System.Collections.Generic;
using FlaUI.UIA3;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
// 競合回避
using TextBox = FlaUI.Core.AutomationElements.TextBox;
using Button = FlaUI.Core.AutomationElements.Button;
using ComboBox = FlaUI.Core.AutomationElements.ComboBox;
using CheckBox = FlaUI.Core.AutomationElements.CheckBox;

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

app.MapGet("/", () => "LWA Agent Ver 2.4 (Read/Write) is Ready! 🤖");

// 書き込みAPI (POST)
app.MapPost("/run", (JobRequest req) => 
{
    Console.WriteLine($"[Order] No:{req.ContainerNo}, Type:{req.Type}, Damaged:{req.IsDamaged}");
    try 
    {
        string result = RunRobot(req);
        return Results.Ok(new { message = "Success", detail = result });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FATAL] {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        return Results.Problem(ex.Message);
    }
});

// 読み取りAPI (GET) - 今回の目玉！
app.MapGet("/inventory", () =>
{
    Console.WriteLine("[Inventory] 在庫一覧取得リクエスト");
    try
    {
        var items = GetInventory();
        return Results.Ok(items);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[FATAL] {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        return Results.Problem(ex.Message);
    }
});

Console.WriteLine("=== LWA Agent Server Started (http://localhost:5000) ===");
app.Run("http://localhost:5000");

// ==========================================
// 2. ロボット制御ロジック (書き込み)
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
        if (tab.Patterns.SelectionItem.IsSupported)
            tab.Patterns.SelectionItem.Pattern.Select();
        else
            tab.Click();
        Thread.Sleep(500);
    }

    Console.WriteLine("DEBUG: 4. コンテナNo入力");
    var elNo = RetryFind(window, "txtContainerNo");
    if (elNo == null) throw new Exception("コンテナNo欄が見つかりません");
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

// ==========================================
// 3. ロボット制御ロジック (読み取り)
// ==========================================
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

    // ★修正1: タブ探しを強化（全体から探す）
    Console.WriteLine("DEBUG: 2. 在庫一覧タブに切り替え");
    var tab = window.FindFirstDescendant(cf => cf.ByAutomationId("TabList")); // RetryFindを使わず、まずは全体検索
    
    if (tab == null)
    {
        // 名前でも探してみる（念のため）
        tab = window.FindFirstDescendant(cf => cf.ByName("在庫一覧"));
    }

    if (tab != null)
    {
        // クリックして切り替え
        if (tab.Patterns.SelectionItem.IsSupported)
            tab.Patterns.SelectionItem.Pattern.Select();
        else
            tab.Click();
            
        Console.WriteLine("  -> タブをクリックしました。描画を待ちます...");
        Thread.Sleep(1000); // ★待ち時間を倍増（1秒待つ）
    }
    else
    {
        throw new Exception("在庫一覧タブが見つかりません（ID: TabList も Name: 在庫一覧 も無し）");
    }

    // ★修正2: グリッド探しを強化（IDで見つからなければ、型で探す）
    Console.WriteLine("DEBUG: 3. グリッド検索");
    
    // まずIDで探す（3秒粘る）
    var elGrid = RetryFind(window, "gridInventory", 3000);
    
    // IDで見つからない場合、"Table" というコントロールタイプで強引に探す
    if (elGrid == null)
    {
        Console.WriteLine("  -> IDで見つからないため、コントロールタイプ(Table)で検索します...");
        elGrid = window.FindFirstDescendant(cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Table));
    }

    if (elGrid == null) throw new Exception("グリッドが見つかりません");

    var items = new List<InventoryItem>();

    // 行を取得
    Console.WriteLine("DEBUG: 4. 行データの読み取り");
    // 行は "Custom" または "DataItem" として認識されることが多い
    var rows = elGrid.FindAllChildren();
    Console.WriteLine($"  -> 子要素数: {rows.Length}");

foreach (var row in rows)
    {
        var cells = row.FindAllChildren();
        
        // ヘッダー行やスクロールバーなどを除外
        // (データ行ならセルが沢山あるはずだが、RowHeaderが含まれるので列数がズレることに注意)
        if (cells.Length >= 4)
        {
            // WinFormsのGridは [0]が「行ヘッダー(矢印が出るところ)」の場合がある。
            // 実際のデータは [1] から始まることが多いが、アプリによる。
            // ここでは「Valueパターン（値）」を持っているセルを優先して探すヘルパーを使う。
            
            string GetVal(AutomationElement cell)
            {
                // 1. まず「値」パターンを持ってるか確認（これが本命）
                if (cell.Patterns.Value.IsSupported)
                {
                    return cell.Patterns.Value.Pattern.Value;
                }
                // 2. なければLegacyパターン（古いアプリ用）
                if (cell.Patterns.LegacyIAccessible.IsSupported)
                {
                    return cell.Patterns.LegacyIAccessible.Pattern.Value;
                }
                // 3. それもなければName（ただし今回のようにゴミが入る可能性あり）
                return cell.Name;
            }

            // 行ヘッダーがある場合、[0]はゴミ、[1]がNo、[2]がType... となるケースが多い
            // とりあえず全セルから「値」を抜いてみる
            var values = cells.Select(c => GetVal(c)).ToList();

            // デバッグ用に全列の中身を表示してみる（コンソールで確認用）
            // Console.WriteLine($"Row: {string.Join(", ", values)}");

            // 値が入っているかチェック（"行 0" みたいなゴミを除外）
            // コンテナNoっぽい文字列（英数字）が含まれているか？
            // ここでは簡易的に、リストのどこかにデータがあるか探してマッピングする
            
            // 例: [0]="", [1]="MOLU-888", [2]="40ft", [3]="なし", [4]="09:00" の場合
            if (values.Count >= 5) 
            {
                 // 行ヘッダー(index 0)をスキップして 1,2,3,4 を採用
                 string no = values[1];
                 string type = values[2];
                 string damaged = values[3];
                 string time = values[4];

                 // ★★★ ここに追加（ゴミ掃除フィルター） ★★★
                 if (no == "No") continue;          // ヘッダー行を無視
                 if (no == "(なし)") continue;      // WinForms特有の「新規追加行」を無視
                 if (string.IsNullOrWhiteSpace(no)) continue; // 空行を無視

                 // ゴミ行（ヘッダーなど）を弾く
                 if (no.Contains("列") || no.Contains("ヘッダー")) continue;
                 if (string.IsNullOrWhiteSpace(no)) continue;

                 items.Add(new InventoryItem(no, type, damaged, time));
                 Console.WriteLine($"  -> 読み取り成功: {no}, {type}");
            }
            else if (values.Count == 4)
            {
                // 行ヘッダーがない場合
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

// ==========================================
// 4. ヘルパーメソッド & データ型
// ==========================================
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