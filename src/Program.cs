using BuildChatBot.Build;
using BuildChatBot.Config;
using BuildChatBot.Persistence;
using BuildChatBot.Telegram;
using DotNetEnv;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Telegram.Bot;
using Telegram.Bot.Polling;

namespace BuildChatBot;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // 1. Load env.local from the current working directory (or via --env <path>)
        var envPath = ParseEnvPath(args);
        List<string> searched = new();
        if (envPath is null) envPath = FindEnvFile(out searched);

        if (envPath is not null)
        {
            Console.WriteLine($"Loading env from {Path.GetFullPath(envPath)}");
            Env.Load(envPath);
        }
        else
        {
            Console.WriteLine("No env.local found — falling back to BotConfig defaults.");
            Console.WriteLine("Searched:");
            foreach (var d in searched) Console.WriteLine($"  - {d}");
            Console.WriteLine("Tip: pass --env /absolute/path/to/env.local to be explicit.");
        }

        // 2. Build host
        using var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration((ctx, builder) =>
            {
                builder.SetBasePath(AppContext.BaseDirectory);
                builder.AddJsonFile("appsettings.json", optional: false);
                builder.AddEnvironmentVariables();
            })
            .UseSerilog((ctx, _, lc) => lc.ReadFrom.Configuration(ctx.Configuration))
            .ConfigureServices((ctx, services) =>
            {
                var cfg = BotConfig.Load(ctx.Configuration);
                cfg.Validate();
                services.AddSingleton(cfg);

                services.AddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(cfg.TelegramBotToken));
                services.AddSingleton<ITelegramSender, HybridTelegramSender>();
                services.AddSingleton<BuildQueue>();
                services.AddSingleton<GitService>();
                services.AddSingleton<FlutterRunner>();
                services.AddSingleton<ApkSigner>();
                services.AddSingleton<ApkCache>();
                services.AddSingleton<ArtifactNamer>();
                services.AddSingleton<GradleCleaner>();
                services.AddSingleton<BuildLogStore>();
                services.AddSingleton<BuildOrchestrator>();
                services.AddSingleton<UpdateRouter>();
                services.AddSingleton<BotCommandRegistrar>();
                services.AddHostedService<TelegramPollingService>();
            })
            .Build();

        try
        {
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Bot host crashed");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    private static string? ParseEnvPath(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--env") return args[i + 1];
        return null;
    }

    private static string? FindEnvFile(out List<string> searched)
    {
        searched = new List<string>();
        // Walk up from BOTH (a) the current working dir — covers `dotnet run` from any folder —
        // and (b) the assembly's base directory — covers running the published DLL directly
        // where CWD is bin/Release/netX/. We walk to the filesystem root.
        var roots = new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };
        foreach (var root in roots)
        {
            string? dir = root;
            while (!string.IsNullOrEmpty(dir))
            {
                var candidate = Path.Combine(dir, "env.local");
                searched.Add(candidate);
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
        }
        return null;
    }
}

internal sealed class TelegramPollingService(
    ITelegramBotClient client,
    UpdateRouter router,
    ITelegramSender sender,
    BotCommandRegistrar commands,
    ILogger<TelegramPollingService> log) : IHostedService
{
    private CancellationTokenSource? _cts;

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var me = await client.GetMe(ct).ConfigureAwait(false);
        log.LogInformation("Bot connected: @{Username} ({Id})", me.Username, me.Id);

        // Publish the slash-command popup list. Idempotent — safe to call every startup.
        await commands.PublishAsync(ct).ConfigureAwait(false);

        await sender.StartAsync(ct).ConfigureAwait(false);

        client.StartReceiving(
            updateHandler: router,
            receiverOptions: new ReceiverOptions { AllowedUpdates = Array.Empty<global::Telegram.Bot.Types.Enums.UpdateType>() },
            cancellationToken: _cts.Token);

        log.LogInformation("Polling started. Send /help to the bot to begin.");
    }

    public Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }
}
