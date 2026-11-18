using System.Collections.Concurrent;
using System.Text.Json;
using ITBees.OOCP.Client;
using ITBees.OOCP.Hosting;
using ITBees.OOCP.Messages;
using ITBees.OOCP.Protocol;
using ITBees.OOCP.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ITBees.OOCP.Tests;

/// <summary>A real ASP.NET Core pipeline (TestServer) with the charge point client on the other side of the socket.</summary>
public class CentralSystemTests : IAsyncLifetime
{
    private IHost _host = null!;
    private RecordingHandler _handler = null!;
    private IOcppCentralSystem _centralSystem = null!;

    public async Task InitializeAsync()
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging();
                    services.AddOcppCentralSystem<RecordingHandler>(options =>
                    {
                        options.HeartbeatIntervalSeconds = 120;
                        options.CallTimeout = TimeSpan.FromSeconds(3);
                    });
                });
                web.Configure(app =>
                {
                    app.UseOcppCentralSystem("/ocpp");
                    app.Run(context => context.Response.WriteAsync("other"));
                });
            })
            .StartAsync();
        _handler = _host.Services.GetRequiredService<RecordingHandler>();
        _centralSystem = _host.Services.GetRequiredService<IOcppCentralSystem>();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<OcppChargePointClient> Connect(string identity)
    {
        var webSocketClient = _host.GetTestServer().CreateWebSocketClient();
        webSocketClient.SubProtocols.Add("ocpp1.6");
        var socket = await webSocketClient.ConnectAsync(new Uri($"ws://localhost/ocpp/{identity}"), CancellationToken.None);
        var client = new OcppChargePointClient { CallTimeout = TimeSpan.FromSeconds(3) };
        client.Attach(socket);
        await WaitUntil(() => _centralSystem.IsConnected(identity));
        return client;
    }

    [Fact]
    public async Task Full_charging_session_reaches_the_handler()
    {
        await using var charger = await Connect("LNZ-001");

        var boot = await charger.BootNotification("Lenergizee", "Business OCPP 22kW", "SN123", "1.0.7");
        Assert.Equal(RegistrationStatus.Accepted, boot.Status);
        Assert.Equal(120, boot.Interval);
        Assert.True(Math.Abs((boot.CurrentTime - DateTime.UtcNow).TotalSeconds) < 5);

        await charger.StatusNotification(1, ChargePointStatus.Available);
        await charger.StatusNotification(1, ChargePointStatus.Preparing);
        var authorize = await charger.Authorize("04A1B2C3D4");
        Assert.Equal(AuthorizationStatus.Accepted, authorize.IdTagInfo.Status);

        var start = await charger.StartTransaction(1, "04A1B2C3D4", 10_000);
        Assert.True(start.TransactionId > 0);
        await charger.StatusNotification(1, ChargePointStatus.Charging);
        await charger.MeterValues(1, start.TransactionId, 11_500, 11_000);
        await charger.StopTransaction(start.TransactionId, 13_250, "EVDisconnected");
        await charger.StatusNotification(1, ChargePointStatus.Available);

        Assert.Equal("Lenergizee", _handler.Boots.Single().ChargePointVendor);
        Assert.Equal(new[] { "Available", "Preparing", "Charging", "Available" }, _handler.Statuses.Select(x => x.Status));
        Assert.Equal(10_000, _handler.Starts.Single().MeterStart);
        Assert.Equal(11_500, MeterReading.From(_handler.Meters.Single().MeterValue).EnergyWh);
        Assert.Equal(13_250, _handler.Stops.Single().MeterStop);
        Assert.Equal(start.TransactionId, _handler.Stops.Single().TransactionId);
        Assert.Equal("LNZ-001", _centralSystem.ConnectedChargePoints.Single().ChargePointId);
        Assert.Equal("1.0.7", _centralSystem.Find("lnz-001")!.BootNotification!.FirmwareVersion);
    }

    [Fact]
    public async Task Commands_reach_the_charger_and_return_its_answer()
    {
        await using var charger = await Connect("CP-CMD");
        var received = new ConcurrentQueue<(string Action, JsonElement Payload)>();
        charger.CommandHandler = (action, payload) =>
        {
            received.Enqueue((action, payload));
            object answer = action switch
            {
                OcppActions.UnlockConnector => new StatusResponse { Status = "Unlocked" },
                OcppActions.Reset => new StatusResponse { Status = "Rejected" },
                OcppActions.GetConfiguration => new GetConfigurationResponse
                {
                    ConfigurationKey = new List<ConfigurationKeyValue> { new() { Key = "HeartbeatInterval", Value = "120" } },
                },
                _ => new StatusResponse { Status = "Accepted" },
            };
            return Task.FromResult(answer);
        };

        Assert.True((await _centralSystem.RemoteStartTransaction("CP-CMD", "OPERATOR", 1)).IsAccepted);
        Assert.True((await _centralSystem.RemoteStopTransaction("CP-CMD", 42)).IsAccepted);
        Assert.True((await _centralSystem.UnlockConnector("CP-CMD", 1)).IsAccepted);
        Assert.False((await _centralSystem.Reset("CP-CMD", hard: false)).IsAccepted);
        Assert.True((await _centralSystem.ChangeAvailability("CP-CMD", 0, operative: false)).IsAccepted);
        var configuration = await _centralSystem.GetConfiguration("CP-CMD", new[] { "HeartbeatInterval" });
        Assert.Equal("120", configuration.ConfigurationKey!.Single().Value);

        var actions = received.Select(x => x.Action).ToList();
        Assert.Equal(new[] { "RemoteStartTransaction", "RemoteStopTransaction", "UnlockConnector", "Reset", "ChangeAvailability", "GetConfiguration" }, actions);
        var stop = received.Single(x => x.Action == "RemoteStopTransaction").Payload;
        Assert.Equal(42, stop.GetProperty("transactionId").GetInt32());
        var availability = received.Single(x => x.Action == "ChangeAvailability").Payload;
        Assert.Equal("Inoperative", availability.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Unknown_charger_or_silence_gives_a_clear_failure()
    {
        var notConnected = await Assert.ThrowsAsync<OcppCallException>(() => _centralSystem.Reset("NOBODY", hard: true));
        Assert.Equal(OcppCallFailure.NotConnected, notConnected.Failure);

        await using var charger = await Connect("CP-SLOW");
        charger.CommandHandler = async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return new StatusResponse { Status = "Accepted" };
        };
        var timeout = await Assert.ThrowsAsync<OcppCallException>(() => _centralSystem.Reset("CP-SLOW", hard: false));
        Assert.Equal(OcppCallFailure.Timeout, timeout.Failure);
    }

    [Fact]
    public async Task Charger_error_answer_is_reported()
    {
        await using var charger = await Connect("CP-ERR");
        charger.CommandHandler = (_, _) => throw new OcppCallErrorException(OcppErrorCodes.NotSupported, "no lock");
        var error = await Assert.ThrowsAsync<OcppCallException>(() => _centralSystem.UnlockConnector("CP-ERR", 1));
        Assert.Equal(OcppCallFailure.CallError, error.Failure);
        Assert.Equal(OcppErrorCodes.NotSupported, error.ErrorCode);
    }

    [Fact]
    public async Task Unknown_action_is_answered_with_not_implemented()
    {
        await using var charger = await Connect("CP-UNK");
        var error = await Assert.ThrowsAsync<OcppCallException>(() => charger.Call<StatusResponse>("FancyVendorThing", new { }));
        Assert.Equal(OcppErrorCodes.NotImplemented, error.ErrorCode);
    }

    [Fact]
    public async Task Reconnect_replaces_the_old_connection_and_disconnect_is_reported()
    {
        var first = await Connect("CP-RE");
        await using var second = await Connect("CP-RE");
        await WaitUntil(() => !first.IsConnected);
        Assert.True(_centralSystem.IsConnected("CP-RE"));
        Assert.Single(_centralSystem.ConnectedChargePoints, x => x.ChargePointId == "CP-RE");

        await second.CloseAsync();
        await WaitUntil(() => !_centralSystem.IsConnected("CP-RE"));
        await WaitUntil(() => _handler.Disconnected.Count(x => x == "CP-RE") >= 2);
        await first.DisposeAsync();
    }

    [Fact]
    public async Task Refused_connection_never_reaches_the_handler()
    {
        _handler.RefuseIdentity = "BLOCKED";
        var webSocketClient = _host.GetTestServer().CreateWebSocketClient();
        await Assert.ThrowsAnyAsync<Exception>(() =>
            webSocketClient.ConnectAsync(new Uri("ws://localhost/ocpp/BLOCKED"), CancellationToken.None));
        Assert.DoesNotContain("BLOCKED", _handler.Connected);
    }

    [Fact]
    public async Task Handler_may_call_the_charger_while_answering_it()
    {
        // Answering BootNotification, the handler asks the charger for its configuration - must not deadlock.
        _handler.AskConfigurationOnBoot = true;
        await using var charger = await Connect("CP-NEST");
        charger.CommandHandler = (_, _) => Task.FromResult<object>(new GetConfigurationResponse
        {
            ConfigurationKey = new List<ConfigurationKeyValue> { new() { Key = "NumberOfConnectors", Value = "1" } },
        });
        var boot = await charger.BootNotification("X", "Y");
        Assert.Equal(RegistrationStatus.Accepted, boot.Status);
        Assert.Equal("1", _handler.ConfigurationSeenOnBoot);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met in time");
            await Task.Delay(20);
        }
    }

    public class RecordingHandler : OcppCentralSystemHandlerBase
    {
        private readonly IServiceProvider _serviceProvider;

        public RecordingHandler(OcppCentralSystemOptions options, IServiceProvider serviceProvider) : base(options)
        {
            _serviceProvider = serviceProvider;
        }

        public ConcurrentQueue<string> Connected { get; } = new();
        public ConcurrentQueue<string> Disconnected { get; } = new();
        public ConcurrentQueue<BootNotificationRequest> Boots { get; } = new();
        public ConcurrentQueue<StatusNotificationRequest> Statuses { get; } = new();
        public ConcurrentQueue<StartTransactionRequest> Starts { get; } = new();
        public ConcurrentQueue<StopTransactionRequest> Stops { get; } = new();
        public ConcurrentQueue<MeterValuesRequest> Meters { get; } = new();
        public string? RefuseIdentity { get; set; }
        public bool AskConfigurationOnBoot { get; set; }
        public string? ConfigurationSeenOnBoot { get; private set; }

        public override Task<OcppConnectionDecision> OnConnecting(OcppConnectionRequest request) =>
            Task.FromResult(request.ChargePointId == RefuseIdentity
                ? OcppConnectionDecision.Rejected(404, "Unknown charge point")
                : OcppConnectionDecision.Accepted());

        public override Task OnConnected(OcppChargePointContext chargePoint)
        {
            Connected.Enqueue(chargePoint.ChargePointId);
            return Task.CompletedTask;
        }

        public override Task OnDisconnected(OcppChargePointContext chargePoint, string reason)
        {
            Disconnected.Enqueue(chargePoint.ChargePointId);
            return Task.CompletedTask;
        }

        public override async Task<BootNotificationResponse> OnBootNotification(OcppChargePointContext chargePoint,
            BootNotificationRequest request)
        {
            Boots.Enqueue(request);
            if (AskConfigurationOnBoot)
            {
                var centralSystem = _serviceProvider.GetRequiredService<IOcppCentralSystem>();
                var configuration = await centralSystem.GetConfiguration(chargePoint.ChargePointId, new[] { "NumberOfConnectors" });
                ConfigurationSeenOnBoot = configuration.ConfigurationKey?.FirstOrDefault()?.Value;
            }

            return await base.OnBootNotification(chargePoint, request);
        }

        public override Task<StatusNotificationResponse> OnStatusNotification(OcppChargePointContext chargePoint,
            StatusNotificationRequest request)
        {
            Statuses.Enqueue(request);
            return base.OnStatusNotification(chargePoint, request);
        }

        public override Task<StartTransactionResponse> OnStartTransaction(OcppChargePointContext chargePoint,
            StartTransactionRequest request)
        {
            Starts.Enqueue(request);
            return base.OnStartTransaction(chargePoint, request);
        }

        public override Task<StopTransactionResponse> OnStopTransaction(OcppChargePointContext chargePoint,
            StopTransactionRequest request)
        {
            Stops.Enqueue(request);
            return base.OnStopTransaction(chargePoint, request);
        }

        public override Task<MeterValuesResponse> OnMeterValues(OcppChargePointContext chargePoint, MeterValuesRequest request)
        {
            Meters.Enqueue(request);
            return base.OnMeterValues(chargePoint, request);
        }
    }
}
