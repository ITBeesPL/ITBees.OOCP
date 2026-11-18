using System.Text.Json;
using ITBees.OOCP.Messages;
using ITBees.OOCP.Protocol;
using ITBees.OOCP.Server;

namespace ITBees.OOCP.Tests;

public class ProtocolTests
{
    [Fact]
    public void Call_is_parsed()
    {
        Assert.True(OcppFrame.TryParse("[2,\"19223201\",\"BootNotification\",{\"chargePointVendor\":\"Lenergizee\",\"chargePointModel\":\"Business\"}]",
            out var frame, out _));
        Assert.Equal(OcppMessageType.Call, frame!.Type);
        Assert.Equal("19223201", frame.UniqueId);
        Assert.Equal("BootNotification", frame.Action);
        var boot = OcppJson.Deserialize<BootNotificationRequest>(frame.Payload)!;
        Assert.Equal("Lenergizee", boot.ChargePointVendor);
    }

    [Fact]
    public void Lenient_frames_are_accepted()
    {
        // Message type as a string, numeric unique id, missing payload.
        Assert.True(OcppFrame.TryParse("[\"2\", 17, \"Heartbeat\"]", out var frame, out _));
        Assert.Equal("17", frame!.UniqueId);
        Assert.Equal(JsonValueKind.Object, frame.Payload.ValueKind);

        Assert.True(OcppFrame.TryParse("[3,\"a\",null]", out var result, out _));
        Assert.Equal(OcppMessageType.CallResult, result!.Type);
        Assert.Equal(JsonValueKind.Object, result.Payload.ValueKind);
    }

    [Fact]
    public void Call_error_is_parsed()
    {
        Assert.True(OcppFrame.TryParse("[4,\"x\",\"NotImplemented\",\"nope\",{}]", out var frame, out _));
        Assert.Equal(OcppMessageType.CallError, frame!.Type);
        Assert.Equal("NotImplemented", frame.ErrorCode);
        Assert.Equal("nope", frame.ErrorDescription);
    }

    [Fact]
    public void Garbage_is_rejected()
    {
        Assert.False(OcppFrame.TryParse("{}", out _, out _));
        Assert.False(OcppFrame.TryParse("[9,\"x\",\"y\",{}]", out _, out _));
        Assert.False(OcppFrame.TryParse("not json", out _, out _));
        Assert.Equal("abc", OcppFrame.TryReadUniqueId("[2,\"abc\",\"Foo\",{bad"));
    }

    [Fact]
    public void Timestamps_are_written_without_fractions()
    {
        var json = OcppJson.Serialize(new HeartbeatResponse { CurrentTime = new DateTime(2026, 11, 18, 10, 15, 30, 123, DateTimeKind.Utc) });
        Assert.Equal("{\"currentTime\":\"2026-11-18T10:15:30Z\"}", json);
    }

    [Theory]
    [InlineData("2026-11-18T10:15:30Z", 10)]
    [InlineData("2026-11-18T10:15:30.123Z", 10)]
    [InlineData("2026-11-18T11:15:30+01:00", 10)]
    [InlineData("2026-11-18T10:15:30", 10)]
    public void Timestamps_are_read_leniently(string text, int expectedUtcHour)
    {
        Assert.True(OcppTimestamps.TryParse(text, out var utc));
        Assert.Equal(expectedUtcHour, utc.Hour);
        Assert.Equal(DateTimeKind.Utc, utc.Kind);
    }

    [Fact]
    public void Unset_charger_clock_falls_back_to_receipt_time()
    {
        var received = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(received, OcppTimestamps.OrReceived(new DateTime(1970, 1, 1, 0, 5, 0, DateTimeKind.Utc), received));
        Assert.Equal(received, OcppTimestamps.OrReceived(received.AddDays(3), received));
        Assert.Equal(received.AddMinutes(-2), OcppTimestamps.OrReceived(received.AddMinutes(-2), received));
        Assert.Equal(received, OcppTimestamps.OrReceived(null, received));
    }

    [Fact]
    public void Numbers_as_strings_and_bad_timestamps_do_not_fail_the_message()
    {
        var start = JsonSerializer.Deserialize<StartTransactionRequest>(
            "{\"connectorId\":\"1\",\"idTag\":\"04A1B2C3\",\"meterStart\":\"1500\",\"timestamp\":\"garbage\"}", OcppJson.Options)!;
        Assert.Equal(1, start.ConnectorId);
        Assert.Equal(1500, start.MeterStart);
        Assert.Equal(DateTime.MinValue, start.Timestamp);
    }

    [Fact]
    public void Status_is_parsed_case_insensitively()
    {
        var status = JsonSerializer.Deserialize<StatusNotificationRequest>(
            "{\"connectorId\":1,\"errorCode\":\"NoError\",\"status\":\"charging\"}", OcppJson.Options)!;
        Assert.Equal(ChargePointStatus.Charging, status.ParsedStatus);
        Assert.False(status.HasError);
        Assert.Equal(ChargePointStatus.Unknown, ChargePointStatuses.Parse("whatever"));
        Assert.True(ChargePointStatuses.IsOccupied(ChargePointStatus.SuspendedEV));
        Assert.False(ChargePointStatuses.IsOccupied(ChargePointStatus.Available));
    }

    [Fact]
    public void Meter_reading_normalises_units_and_phases()
    {
        var reading = MeterReading.From(new List<MeterValue>
        {
            new()
            {
                Timestamp = DateTime.UtcNow,
                SampledValue = new List<SampledValue>
                {
                    new() { Value = "12.5", Measurand = "Energy.Active.Import.Register", Unit = "kWh" },
                    new() { Value = "3.6", Measurand = "Power.Active.Import", Unit = "kW", Phase = "L1" },
                    new() { Value = "3.7", Measurand = "Power.Active.Import", Unit = "kW", Phase = "L2" },
                    new() { Value = "3.7", Measurand = "Power.Active.Import", Unit = "kW", Phase = "L3" },
                    new() { Value = "16", Measurand = "Current.Import", Unit = "A", Phase = "L1" },
                    new() { Value = "15,5", Measurand = "Current.Import", Unit = "A", Phase = "L2" },
                    new() { Value = "230", Measurand = "Voltage", Unit = "V", Phase = "L1-N" },
                    new() { Value = "400", Measurand = "Voltage", Unit = "V", Phase = "L1-L2" },
                    new() { Value = "999", Measurand = "Energy.Active.Import.Register", Location = "Inlet" },
                },
            },
        });

        Assert.Equal(12500, reading.EnergyWh);
        Assert.Equal(11000, reading.PowerW!.Value, 3);
        Assert.Equal(16, reading.CurrentA);
        Assert.Equal(230, reading.VoltageV);
    }

    [Fact]
    public void Meter_reading_without_measurand_is_the_energy_register()
    {
        var reading = MeterReading.From(new List<MeterValue>
        {
            new() { Timestamp = DateTime.UtcNow, SampledValue = new List<SampledValue> { new() { Value = "4321" } } },
        });
        Assert.Equal(4321, reading.EnergyWh);
    }

    [Fact]
    public void Transaction_ids_increase_and_survive_a_restart()
    {
        var now = new DateTime(2026, 11, 18, 10, 0, 0, DateTimeKind.Utc);
        var generator = new OcppTransactionIdGenerator(() => now);
        var first = generator.Next();
        var second = generator.Next();
        Assert.True(second > first);

        // A new process a few seconds later starts above what the old one handed out.
        var restarted = new OcppTransactionIdGenerator(() => now.AddSeconds(5));
        Assert.True(restarted.Next() > second);
    }

    [Fact]
    public void Basic_auth_password_is_read()
    {
        var header = "Basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("CP01:secret:with:colons"));
        Assert.Equal("secret:with:colons", OcppCentralSystem.ReadBasicAuthPassword(header));
        Assert.Null(OcppCentralSystem.ReadBasicAuthPassword("Bearer x"));
        Assert.Null(OcppCentralSystem.ReadBasicAuthPassword(null));
    }
}
