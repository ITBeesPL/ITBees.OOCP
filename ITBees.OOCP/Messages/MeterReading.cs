using System.Globalization;

namespace ITBees.OOCP.Messages;

/// <summary>
/// The values a parking needs out of MeterValues / StopTransaction.transactionData, normalised to Wh, W, A and V.
/// Chargers differ a lot: energy in Wh or kWh, power in W or kW, values per phase or totals, the measurand left out
/// (= energy register). Totals win over per-phase values; per-phase power/current are summed / averaged when no total
/// is sent.
/// </summary>
public sealed class MeterReading
{
    public const string EnergyActiveImportRegister = "Energy.Active.Import.Register";
    public const string PowerActiveImport = "Power.Active.Import";
    public const string CurrentImport = "Current.Import";
    public const string Voltage = "Voltage";
    public const string SoC = "SoC";

    public DateTime? TimestampUtc { get; private set; }

    /// <summary>Energy register (meter reading), Wh.</summary>
    public double? EnergyWh { get; private set; }

    /// <summary>Active power drawn by the vehicle, W.</summary>
    public double? PowerW { get; private set; }

    /// <summary>Current per phase (the highest phase when phases are sent separately), A.</summary>
    public double? CurrentA { get; private set; }

    /// <summary>Voltage (average of the phases), V.</summary>
    public double? VoltageV { get; private set; }

    /// <summary>Battery state of charge, % (DC chargers mostly).</summary>
    public double? SoCPercent { get; private set; }

    public bool IsEmpty => EnergyWh == null && PowerW == null && CurrentA == null && VoltageV == null && SoCPercent == null;

    /// <summary>The newest value of every measurand found in <paramref name="meterValues"/>.</summary>
    public static MeterReading From(IEnumerable<MeterValue>? meterValues)
    {
        var reading = new MeterReading();
        if (meterValues == null)
            return reading;

        foreach (var meterValue in meterValues.Where(x => x != null).OrderBy(x => x.Timestamp))
        {
            var sample = FromOne(meterValue);
            reading.TimestampUtc = meterValue.Timestamp == DateTime.MinValue ? reading.TimestampUtc : meterValue.Timestamp;
            reading.EnergyWh = sample.EnergyWh ?? reading.EnergyWh;
            reading.PowerW = sample.PowerW ?? reading.PowerW;
            reading.CurrentA = sample.CurrentA ?? reading.CurrentA;
            reading.VoltageV = sample.VoltageV ?? reading.VoltageV;
            reading.SoCPercent = sample.SoCPercent ?? reading.SoCPercent;
        }

        return reading;
    }

    private static MeterReading FromOne(MeterValue meterValue)
    {
        var reading = new MeterReading();
        var energyTotal = (double?)null;
        var energyPhases = new List<double>();
        var powerTotal = (double?)null;
        var powerPhases = new List<double>();
        var currentTotal = (double?)null;
        var currentPhases = new List<double>();
        var voltages = new List<double>();

        foreach (var sampled in meterValue.SampledValue ?? new List<SampledValue>())
        {
            if (sampled == null || !TryParseNumber(sampled.Value, out var value))
                continue;
            // Only what the vehicle takes: "Inlet"/"EV" locations and export registers are skipped.
            if (!string.IsNullOrEmpty(sampled.Location) &&
                !sampled.Location.Equals("Outlet", StringComparison.OrdinalIgnoreCase) &&
                !sampled.Location.Equals("Body", StringComparison.OrdinalIgnoreCase) &&
                !sampled.Location.Equals("Cable", StringComparison.OrdinalIgnoreCase))
                continue;

            var measurand = string.IsNullOrWhiteSpace(sampled.Measurand) ? EnergyActiveImportRegister : sampled.Measurand.Trim();
            var unit = sampled.Unit?.Trim() ?? string.Empty;
            var perPhase = IsSinglePhase(sampled.Phase);

            if (measurand.Equals(EnergyActiveImportRegister, StringComparison.OrdinalIgnoreCase))
            {
                var wh = unit.Equals("kWh", StringComparison.OrdinalIgnoreCase) ? value * 1000 : value;
                if (perPhase) energyPhases.Add(wh); else energyTotal = wh;
            }
            else if (measurand.Equals(PowerActiveImport, StringComparison.OrdinalIgnoreCase))
            {
                var w = unit.Equals("kW", StringComparison.OrdinalIgnoreCase) ? value * 1000 : value;
                if (perPhase) powerPhases.Add(w); else powerTotal = w;
            }
            else if (measurand.Equals(CurrentImport, StringComparison.OrdinalIgnoreCase))
            {
                if (perPhase) currentPhases.Add(value); else currentTotal = value;
            }
            else if (measurand.Equals(Voltage, StringComparison.OrdinalIgnoreCase))
            {
                // Phase-to-phase readings (L1-L2) are not what the panel shows; phase-to-neutral (L1-N) are.
                if (!IsPhaseToPhase(sampled.Phase))
                    voltages.Add(value);
            }
            else if (measurand.Equals(SoC, StringComparison.OrdinalIgnoreCase))
            {
                reading.SoCPercent = value;
            }
        }

        reading.EnergyWh = energyTotal ?? (energyPhases.Count > 0 ? energyPhases.Sum() : null);
        reading.PowerW = powerTotal ?? (powerPhases.Count > 0 ? powerPhases.Sum() : null);
        reading.CurrentA = currentTotal ?? (currentPhases.Count > 0 ? currentPhases.Max() : null);
        reading.VoltageV = voltages.Count > 0 ? voltages.Average() : null;
        return reading;
    }

    private static bool IsPhaseToPhase(string? phase) =>
        phase != null && phase.Contains("-L", StringComparison.OrdinalIgnoreCase);

    private static bool IsSinglePhase(string? phase) =>
        !string.IsNullOrWhiteSpace(phase) && !IsPhaseToPhase(phase) &&
        (phase.StartsWith("L1", StringComparison.OrdinalIgnoreCase) ||
         phase.StartsWith("L2", StringComparison.OrdinalIgnoreCase) ||
         phase.StartsWith("L3", StringComparison.OrdinalIgnoreCase));

    /// <summary>Accepts "1234", "1234.5" and "1234,5".</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var normalized = text.Trim().Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
