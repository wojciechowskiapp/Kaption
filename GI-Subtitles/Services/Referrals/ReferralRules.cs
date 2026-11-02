using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GI_Subtitles.Services.Network;

namespace GI_Subtitles.Services.Referrals;

/// <summary>
/// Client copy of the claim and attribution contract in backend/src/lib/referrals.ts
/// (validateClaimInput) and backend/src/routes/referrals.ts. Change them together.
/// </summary>
public static partial class ReferralRules
{
    public const string Paypal = "paypal";
    public const string AmazonGiftCard = "amazon_gc";
    public const string HoyolabUid = "hoyolab_uid";

    private const string RateLimitedKey = "Referrals_Error_RateLimited";
    private const int MaxEmailLength = 200;
    private static readonly string[] RegionTags = ["na", "eu", "asia", "tw_hk_mo"];

    public static IReadOnlyList<string> Regions => RegionTags;

    // \z, not $: .NET's $ also matches before a trailing newline, JavaScript's does not.
    [GeneratedRegex(@"^[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}\z", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"^[A-Za-z0-9]{8,12}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UidPattern();

    public static bool IsValidEmail(string email) =>
        !string.IsNullOrEmpty(email) && email.Length <= MaxEmailLength && EmailPattern().IsMatch(email);

    public static bool IsValidUid(string uid) => !string.IsNullOrEmpty(uid) && UidPattern().IsMatch(uid);

    public static bool IsValidRegion(string region) => region is not null && Array.IndexOf(RegionTags, region) >= 0;

    public static ClaimContactInfo ContactForEmail(string email) => new() { Email = email?.Trim() };

    public static ClaimContactInfo ContactForHoyolab(string uid, string region) =>
        new() { Uid = uid?.Trim(), Region = region };

    /// <summary>String resource key for a rejected claim, from the server's error code or status.</summary>
    public static string ClaimErrorKey(int status, string code) => code switch
    {
        "invalid_hoyolab_uid" => "Referrals_Claim_Error_InvalidUid",
        "invalid_hoyolab_region" => "Referrals_Claim_Error_InvalidRegion",
        "invalid_email" => "Referrals_Claim_Error_InvalidEmail",
        "not_claimable" => "Referrals_Claim_Error_AlreadyClaimed",
        "not_found" => "Referrals_Claim_Error_NotFound",
        "rate_limited" => RateLimitedKey,
        _ => status switch
        {
            404 => "Referrals_Claim_Error_NotFound",
            409 => "Referrals_Claim_Error_AlreadyClaimed",
            429 => RateLimitedKey,
            _ => "Referrals_Claim_Error_Rejected",
        },
    };

    /// <summary>String resource key for a rejected referral code.</summary>
    public static string AttributionErrorKey(int status, string code) => code switch
    {
        "self_referral" => "Referrals_Attribute_SelfReferral",
        "already_attributed" => "Referrals_Attribute_Conflict",
        "attribution_window_closed" => "Referrals_Attribute_PastWindow",
        "unknown_code" or "invalid_code" => "Referrals_Attribute_Invalid",
        "rate_limited" => RateLimitedKey,
        _ => status == 429 ? RateLimitedKey : "Referrals_Attribute_Rejected",
    };

    /// <summary>Polish needs a separate singular ("1 dzień", not "1 dni"); a count of one uses the <c>_One_Format</c> key.</summary>
    public static string CountKey(string formatKey, int count)
    {
        const string suffix = "_Format";
        return count == 1 && formatKey.EndsWith(suffix, StringComparison.Ordinal)
            ? formatKey[..^suffix.Length] + "_One" + suffix
            : formatKey;
    }
}
