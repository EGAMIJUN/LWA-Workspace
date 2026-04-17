using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Button = FlaUI.Core.AutomationElements.Button;
using CheckBox = FlaUI.Core.AutomationElements.CheckBox;
using TextBox = FlaUI.Core.AutomationElements.TextBox;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

var agentOptions = LoadAgentOptions(builder.Configuration);

builder.Services.AddCors(corsOptions =>
{
    corsOptions.AddPolicy("AgentCors", policy =>
    {
        if (agentOptions.AllowedOrigins.Length > 0)
        {
            policy.WithOrigins(agentOptions.AllowedOrigins).AllowAnyMethod().AllowAnyHeader();
        }
    });
});

var app = builder.Build();
app.UseCors("AgentCors");

var logger = app.Logger;
var robotSemaphore = new SemaphoreSlim(1, 1);
var allowedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "20ft Dry",
    "40ft Dry",
    "40ft Reefer",
    "40ft OpenTop"
};

var pollingCts = new CancellationTokenSource();
app.Lifetime.ApplicationStopping.Register(() => pollingCts.Cancel());
_ = Task.Run(() => StartSqsPolling(agentOptions, robotSemaphore, logger, allowedTypes, pollingCts.Token));

app.MapGet("/", () => Results.Ok(new { service = "LWA Agent", status = "running" }));
app.MapGet("/health", () => Results.Ok(new { status = "ok", sqsQueue = agentOptions.SqsUrl }));

app.MapPost("/run", async (HttpContext context, JobRequest req, CancellationToken cancellationToken) =>
{
    if (!IsAuthorized(context, agentOptions.ApiKey))
    {
        return Results.Unauthorized();
    }

    var validationError = ValidateJobRequest(req, allowedTypes);
    if (validationError is not null)
    {
        return Results.BadRequest(new { message = validationError });
    }

    try
    {
        await robotSemaphore.WaitAsync(cancellationToken);
        try
        {
            var result = RunRobot(req);
            return Results.Ok(new { message = "Success", detail = result });
        }
        finally
        {
            robotSemaphore.Release();
        }
    }
    catch (OperationCanceledException)
    {
        return Results.Problem("Request was cancelled.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Robot execution failed.");
        return Results.Problem("Robot execution failed.");
    }
});

app.MapGet("/inventory", async (HttpContext context, CancellationToken cancellationToken) =>
{
    if (!IsAuthorized(context, agentOptions.ApiKey))
    {
        return Results.Unauthorized();
    }

    try
    {
        await robotSemaphore.WaitAsync(cancellationToken);
        try
        {
            var items = GetInventory();
            return Results.Ok(items);
        }
        finally
        {
            robotSemaphore.Release();
        }
    }
    catch (OperationCanceledException)
    {
        return Results.Problem("Request was cancelled.");
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Inventory read failed.");
        return Results.Problem("Inventory read failed.");
    }
});

logger.LogInformation("LWA Agent started at http://localhost:5000");
logger.LogInformation("SQS polling started: {QueueUrl}", agentOptions.SqsUrl);

app.Run("http://localhost:5000");

async Task StartSqsPolling(
    AgentOptions options,
    SemaphoreSlim semaphore,
    ILogger pollingLogger,
    HashSet<string> validTypes,
    CancellationToken cancellationToken)
{
    using var sqsClient = CreateSqsClient(options);
    pollingLogger.LogInformation("SQS client initialized. Waiting for commands...");

    while (!cancellationToken.IsCancellationRequested)
    {
        try
        {
            var request = new ReceiveMessageRequest
            {
                QueueUrl = options.SqsUrl,
                MaxNumberOfMessages = 1,
                WaitTimeSeconds = 20,
                VisibilityTimeout = 60
            };

            var response = await sqsClient.ReceiveMessageAsync(request, cancellationToken);
            if (response.Messages.Count == 0)
            {
                continue;
            }

            var message = response.Messages[0];
            pollingLogger.LogInformation("SQS message received: {MessageId}", message.MessageId);

            if (!TryDeserializeJob(message.Body, out var job))
            {
                pollingLogger.LogWarning("Message deserialization failed. MessageId: {MessageId}", message.MessageId);
                await sqsClient.DeleteMessageAsync(options.SqsUrl, message.ReceiptHandle, cancellationToken);
                continue;
            }

            var validationError = ValidateJobRequest(job, validTypes);
            if (validationError is not null)
            {
                pollingLogger.LogWarning("Invalid job request from queue: {ValidationError}", validationError);
                await sqsClient.DeleteMessageAsync(options.SqsUrl, message.ReceiptHandle, cancellationToken);
                continue;
            }

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var result = RunRobot(job);
                pollingLogger.LogInformation("Robot execution succeeded: {Result}", result);
                await sqsClient.DeleteMessageAsync(options.SqsUrl, message.ReceiptHandle, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            pollingLogger.LogError(ex, "SQS polling error.");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    pollingLogger.LogInformation("SQS polling stopped.");
}

static AgentOptions LoadAgentOptions(IConfiguration configuration)
{
    var sqsUrl = configuration["LWA_SQS_URL"] ?? string.Empty;
    var region = configuration["LWA_AWS_REGION"] ?? "ap-northeast-1";
    var accessKey = configuration["LWA_ACCESS_KEY"] ?? string.Empty;
    var secretKey = configuration["LWA_SECRET_KEY"] ?? string.Empty;
    var apiKey = configuration["LWA_API_KEY"] ?? string.Empty;
    var allowedOrigins = (configuration["LWA_ALLOWED_ORIGINS"] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new InvalidOperationException("LWA_API_KEY must be configured.");
    }

    if (string.IsNullOrWhiteSpace(sqsUrl))
    {
        throw new InvalidOperationException("LWA_SQS_URL must be configured.");
    }

    if (allowedOrigins.Length == 0)
    {
        throw new InvalidOperationException("LWA_ALLOWED_ORIGINS must be configured.");
    }

    foreach (var origin in allowedOrigins)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException($"Invalid CORS origin: {origin}");
        }
    }

    var hasAccessKey = !string.IsNullOrWhiteSpace(accessKey);
    var hasSecretKey = !string.IsNullOrWhiteSpace(secretKey);
    if (hasAccessKey != hasSecretKey)
    {
        throw new InvalidOperationException("LWA_ACCESS_KEY and LWA_SECRET_KEY must be set together.");
    }

    return new AgentOptions(
        sqsUrl,
        region,
        accessKey,
        secretKey,
        apiKey,
        allowedOrigins);
}

static AmazonSQSClient CreateSqsClient(AgentOptions options)
{
    var sqsConfig = new AmazonSQSConfig
    {
        RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region)
    };

    if (!string.IsNullOrWhiteSpace(options.AccessKey))
    {
        return new AmazonSQSClient(options.AccessKey, options.SecretKey, sqsConfig);
    }

    return new AmazonSQSClient(sqsConfig);
}

static bool IsAuthorized(HttpContext context, string expectedApiKey)
{
    if (!context.Request.Headers.TryGetValue("X-API-Key", out var providedHeaderValue))
    {
        return false;
    }

    var providedApiKey = providedHeaderValue.ToString();
    if (string.IsNullOrWhiteSpace(providedApiKey))
    {
        return false;
    }

    var expectedBytes = Encoding.UTF8.GetBytes(expectedApiKey);
    var providedBytes = Encoding.UTF8.GetBytes(providedApiKey);
    if (expectedBytes.Length != providedBytes.Length)
    {
        return false;
    }

    return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
}

static bool TryDeserializeJob(string body, out JobRequest job)
{
    job = default!;

    try
    {
        var parsed = JsonSerializer.Deserialize<JobRequest>(body, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (parsed is null)
        {
            return false;
        }

        job = parsed;
        return true;
    }
    catch
    {
        return false;
    }
}

static string? ValidateJobRequest(JobRequest req, HashSet<string> allowedTypes)
{
    if (string.IsNullOrWhiteSpace(req.ContainerNo))
    {
        return "ContainerNo is required.";
    }

    if (req.ContainerNo.Length > 20)
    {
        return "ContainerNo must be 20 characters or less.";
    }

    if (!Regex.IsMatch(req.ContainerNo, "^[A-Za-z0-9-]+$"))
    {
        return "ContainerNo can only contain letters, numbers, and hyphens.";
    }

    if (string.IsNullOrWhiteSpace(req.Type) || !allowedTypes.Contains(req.Type))
    {
        return "Type is invalid.";
    }

    return null;
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
file sealed record AgentOptions(
    string SqsUrl,
    string Region,
    string AccessKey,
    string SecretKey,
    string ApiKey,
    string[] AllowedOrigins);
