using System.Text.RegularExpressions;

namespace iucs.meettomanage.api.Saas;

/// <summary>How the GST on one invoice is split. Exactly one of (Cgst+Sgst) or Igst is non-zero when tax applies.</summary>
public sealed record GstSplit(decimal Cgst, decimal Sgst, decimal Igst, string? PlaceOfSupply, bool IntraState);

/// <summary>
/// GST place-of-supply rules for the platform's own subscription invoices, as pure functions.
/// A supply within the supplier's state is taxed as CGST + SGST (half each); a supply to another
/// state is taxed as IGST. For services the place of supply is the recipient's location: the state in
/// their GSTIN when registered, otherwise the state on their billing profile.
/// </summary>
public static partial class GstRules
{
    /// <summary>GST state / UT codes (the first two digits of a GSTIN).</summary>
    public static readonly IReadOnlyDictionary<string, string> StateNames = new Dictionary<string, string>
    {
        ["01"] = "Jammu and Kashmir", ["02"] = "Himachal Pradesh", ["03"] = "Punjab", ["04"] = "Chandigarh",
        ["05"] = "Uttarakhand", ["06"] = "Haryana", ["07"] = "Delhi", ["08"] = "Rajasthan", ["09"] = "Uttar Pradesh",
        ["10"] = "Bihar", ["11"] = "Sikkim", ["12"] = "Arunachal Pradesh", ["13"] = "Nagaland", ["14"] = "Manipur",
        ["15"] = "Mizoram", ["16"] = "Tripura", ["17"] = "Meghalaya", ["18"] = "Assam", ["19"] = "West Bengal",
        ["20"] = "Jharkhand", ["21"] = "Odisha", ["22"] = "Chhattisgarh", ["23"] = "Madhya Pradesh", ["24"] = "Gujarat",
        ["26"] = "Dadra and Nagar Haveli and Daman and Diu", ["27"] = "Maharashtra", ["29"] = "Karnataka", ["30"] = "Goa",
        ["31"] = "Lakshadweep", ["32"] = "Kerala", ["33"] = "Tamil Nadu", ["34"] = "Puducherry",
        ["35"] = "Andaman and Nicobar Islands", ["36"] = "Telangana", ["37"] = "Andhra Pradesh", ["38"] = "Ladakh",
    };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["orissa"] = "21", ["pondicherry"] = "34", ["nct of delhi"] = "07", ["new delhi"] = "07", ["delhi ncr"] = "07",
        ["jammu & kashmir"] = "01", ["jammu and kashmir"] = "01", ["andaman & nicobar islands"] = "35",
        ["dadra and nagar haveli"] = "26", ["daman and diu"] = "26", ["daman & diu"] = "26", ["uttaranchal"] = "05",
    };

    [GeneratedRegex(@"^\d{2}[A-Z]{5}\d{4}[A-Z][1-9A-Z]Z[0-9A-Z]$")]
    private static partial Regex GstinShape();

    public static bool LooksLikeGstin(string? gstin) => !string.IsNullOrWhiteSpace(gstin) && GstinShape().IsMatch(gstin.Trim().ToUpperInvariant());

    /// <summary>State code from a well-formed GSTIN, or null (also null for an unknown state prefix).</summary>
    public static string? StateCodeFromGstin(string? gstin)
    {
        if (!LooksLikeGstin(gstin)) return null;
        var code = gstin!.Trim()[..2];
        return StateNames.ContainsKey(code) ? code : null;
    }

    /// <summary>State code from a state name ("Karnataka", "Orissa") or a 2-digit code; null when it can't be matched.</summary>
    public static string? StateCodeFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var text = name.Trim();
        if (text.Length == 2 && StateNames.ContainsKey(text)) return text;
        if (Aliases.TryGetValue(text, out var alias)) return alias;
        foreach (var (code, stateName) in StateNames)
            if (string.Equals(stateName, text, StringComparison.OrdinalIgnoreCase)) return code;
        return null;
    }

    public static string? PlaceOfSupplyLabel(string? stateCode) =>
        stateCode is not null && StateNames.TryGetValue(stateCode, out var name) ? $"{stateCode}-{name}" : null;

    /// <summary>
    /// Splits <paramref name="taxAmount"/>. When the supplier's state is unknown nothing can be split, so the whole
    /// amount stays unsplit (all three parts zero) rather than being labelled wrongly. An unknown recipient state is
    /// treated as the supplier's own state, the safe default for an unregistered buyer with no address.
    /// </summary>
    public static GstSplit Split(decimal taxAmount, string? sellerStateCode, string? buyerGstin, string? buyerState)
    {
        if (sellerStateCode is null || taxAmount <= 0m) return new(0m, 0m, 0m, null, true);
        var supplyState = StateCodeFromGstin(buyerGstin) ?? StateCodeFromName(buyerState) ?? sellerStateCode;
        var label = PlaceOfSupplyLabel(supplyState);
        if (supplyState != sellerStateCode) return new(0m, 0m, taxAmount, label, false);
        var cgst = SaasRules.Round(taxAmount / 2m);
        return new(cgst, taxAmount - cgst, 0m, label, true);
    }
}
