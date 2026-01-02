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
using FlaUI.UIA3;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using TextBox = FlaUI.Core.AutomationElements.TextBox;
using Button = FlaUI.Core.AutomationElements.Button;
using ComboBox = FlaUI.Core.AutomationElements.ComboBox;
using CheckBox = FlaUI.Core.AutomationElements.CheckBox;
using DataGrid = FlaUI.Core.AutomationElements.DataGrid;
using System.Collections.Generic;

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

app.MapGet("/", () => "LWA Agent Ver 2.3 (Final) is Ready! 🤖");

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
        Console.WriteLine(ex.StackTrace); // 詳細な場所を出す
        return Results.Problem(ex.Message);
    }
});

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
// 2. ロボット制御ロジック
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

    // タブ切り替え
    Console.WriteLine("DEBUG: 3. タブ切り替え");
    var tab = RetryFind(window, "TabInput");
    if (tab != null)
    {
        if (tab.Patterns.SelectionItem.IsSupported)
            tab.Patterns.SelectionItem.Pattern.Select();
        else
            tab.Click();
        Thread.Sleep(500); // 画面切り替え待ち
    }

    // 1. コンテナNo入力
    Console.WriteLine("DEBUG: 4. コンテナNo入力");
    var elNo = RetryFind(window, "txtContainerNo");
    if (elNo == null) throw new Exception("コンテナNo欄が見つかりません");
    new TextBox(elNo.FrameworkAutomationElement).Text = req.ContainerNo;

    // 2. タイプ選択 (コンボボックス攻略版)
    Console.WriteLine("DEBUG: 5. コンボボックス検索");
    var elType = RetryFind(window, "cmbContainerType");
    if (elType == null) throw new Exception("タイプ選択欄が見つかりません");
    
  // ★★★ 修正箇所：ここから ★★★
    Console.WriteLine($"DEBUG: 6. コンボボックス操作 (Keyboard): {req.Type}");
    var elCmb = RetryFind(window, "cmbContainerType"); // 再取得
    if (elCmb == null) throw new Exception("コンボボックスが見失いました");

    // 1. コンボボックスにフォーカスを当てる（これをしないと文字が打てない）
    elCmb.Focus();
    Thread.Sleep(300); // フォーカス移動待ち

    // 2. キーボードで文字を直接打ち込む！
    // WinFormsのコンボボックスは、文字を打てばその項目にジャンプする機能がある
    FlaUI.Core.Input.Keyboard.Type(req.Type);
    Thread.Sleep(500); // 選択が追いつくのを待つ

    // 3. 念のため Enter キーで確定
    FlaUI.Core.Input.Keyboard.Type(FlaUI.Core.WindowsAPI.VirtualKeyShort.ENTER);
    Console.WriteLine($"  -> キーボードで '{req.Type}' を打ち込みました");
    // ★★★ 修正ここまで ★★★
      
    // 3. ダメージ有無
    Console.WriteLine("DEBUG: 7. チェックボックス操作");
    var elChk = RetryFind(window, "chkDamaged");
    if (elChk != null)
    {
        var chk = new CheckBox(elChk.FrameworkAutomationElement);
        chk.IsChecked = req.IsDamaged;
    }

    // 4. 登録ボタン
    Console.WriteLine("DEBUG: 8. ボタン押下");
    var elBtn = RetryFind(window, "btnRegister");
    if (elBtn == null) throw new Exception("登録ボタンが見つかりません");
    
    new Button(elBtn.FrameworkAutomationElement).Invoke();

    return $"登録完了: {req.ContainerNo}";
}

static List<InventoryItem> GetInventory()
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

    // タブ切り替え（在庫一覧タブ）
    Console.WriteLine("DEBUG: 3. 在庫一覧タブに切り替え");
    var tab = RetryFind(window, "TabList");
    if (tab != null)
    {
        if (tab.Patterns.SelectionItem.IsSupported)
            tab.Patterns.SelectionItem.Pattern.Select();
        else
            tab.Click();
        Thread.Sleep(500); // 画面切り替え待ち
    }
    else
    {
        throw new Exception("在庫一覧タブが見つかりません");
    }

    // グリッドを探す
    Console.WriteLine("DEBUG: 4. グリッド検索");
    var elGrid = RetryFind(window, "gridInventory");
    if (elGrid == null) throw new Exception("グリッドが見つかりません");

    var grid = new DataGrid(elGrid.FrameworkAutomationElement);
    var items = new List<InventoryItem>();

    // グリッドの全行をループ
    Console.WriteLine("DEBUG: 5. グリッド行の読み取り");
    var rows = grid.Rows;
    Console.WriteLine($"  -> 行数: {rows.Length}");

    foreach (var row in rows)
    {
        var cells = row.Cells;
        if (cells.Length >= 4)
        {
            string no = cells[0].Value ?? "";
            string type = cells[1].Value ?? "";
            string damaged = cells[2].Value ?? "";
            string time = cells[3].Value ?? "";
            
            items.Add(new InventoryItem(no, type, damaged, time));
            Console.WriteLine($"  -> 読み取り: No={no}, Type={type}, Damaged={damaged}, Time={time}");
        }
    }

    Console.WriteLine($"DEBUG: 6. 完了（{items.Count}件）");
    return items;
}

// ==========================================
// 3. ヘルパーメソッド & データ型
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