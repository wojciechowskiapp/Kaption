using System;
using System.Linq;
using System.Text.Json;
using System.Windows;
using GI_Subtitles.Common;
using GI_Subtitles.Services.Detection;
using GI_Subtitles.Services.Network;
using GI_Subtitles.Services.Referrals;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GI_Test;

/// <summary>
/// Desktop side of the referral contract in backend/src/lib/referrals.ts and routes/referrals.ts.
/// Every claim used to fail with 400 because contact_info went out as a string.
/// </summary>
[TestClass]
public class ReferralRulesTests
{
    private static string Serialize(ClaimReferralRewardRequest request) =>
        JsonSerializer.Serialize(request, request.GetType(), JsonDefaults.IgnoreNulls);

    [TestMethod]
    public void HoyolabClaim_SendsUidAndRegionAsAnObject()
    {
        var request = new ClaimReferralRewardRequest(
            ReferralRules.HoyolabUid, ReferralRules.ContactForHoyolab(" 81234567 ", "tw_hk_mo"));

        Assert.AreEqual(
            """{"delivery_method":"hoyolab_uid","contact_info":{"uid":"81234567","region":"tw_hk_mo"}}""",
            Serialize(request));
    }

    [TestMethod]
    public void EmailClaims_SendOnlyTheEmail()
    {
        var paypal = new ClaimReferralRewardRequest(ReferralRules.Paypal, ReferralRules.ContactForEmail(" me@example.com "));
        var amazon = new ClaimReferralRewardRequest(ReferralRules.AmazonGiftCard, ReferralRules.ContactForEmail("me@example.com"));

        Assert.AreEqual("""{"delivery_method":"paypal","contact_info":{"email":"me@example.com"}}""", Serialize(paypal));
        Assert.AreEqual("""{"delivery_method":"amazon_gc","contact_info":{"email":"me@example.com"}}""", Serialize(amazon));
    }

    [TestMethod]
    [DataRow("81234567", true)]
    [DataRow("812345678", true)]
    [DataRow("abcd1234XYZ0", true)]
    [DataRow("1234567", false)]
    [DataRow("1234567890123", false)]
    [DataRow("8123 4567", false)]
    [DataRow("81234-567", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void Uid_MatchesTheServerRule(string uid, bool valid) =>
        Assert.AreEqual(valid, ReferralRules.IsValidUid(uid), uid ?? "(null)");

    [TestMethod]
    [DataRow("a.b+c@ex-ample.co.uk", true)]
    [DataRow("player_1@mail.pl", true)]
    [DataRow("a@b", false)]
    [DataRow("a@b.c", false)]
    [DataRow("a b@c.de", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void Email_MatchesTheServerRule(string email, bool valid) =>
        Assert.AreEqual(valid, ReferralRules.IsValidEmail(email), email ?? "(null)");

    [TestMethod]
    public void Email_LongerThanTheServerCap_IsRejected() =>
        Assert.IsFalse(ReferralRules.IsValidEmail(new string('a', 190) + "@example.com"));

    [TestMethod]
    public void Regions_AreTheServerTags()
    {
        CollectionAssert.AreEqual(new[] { "na", "eu", "asia", "tw_hk_mo" }, ReferralRules.Regions.ToArray());
        Assert.IsFalse(ReferralRules.IsValidRegion("TW"));
        Assert.IsFalse(ReferralRules.IsValidRegion("NA"));
        Assert.IsTrue(ReferralRules.IsValidRegion("tw_hk_mo"));
    }

    [TestMethod]
    [DataRow(400, "invalid_hoyolab_uid", "Referrals_Claim_Error_InvalidUid")]
    [DataRow(400, "invalid_hoyolab_region", "Referrals_Claim_Error_InvalidRegion")]
    [DataRow(400, "invalid_email", "Referrals_Claim_Error_InvalidEmail")]
    [DataRow(409, "not_claimable", "Referrals_Claim_Error_AlreadyClaimed")]
    [DataRow(404, "not_found", "Referrals_Claim_Error_NotFound")]
    [DataRow(429, "rate_limited", "Referrals_Error_RateLimited")]
    [DataRow(400, "invalid_contact_info", "Referrals_Claim_Error_Rejected")]
    [DataRow(409, null, "Referrals_Claim_Error_AlreadyClaimed")]
    [DataRow(418, null, "Referrals_Claim_Error_Rejected")]
    public void ClaimErrors_MapToReadableMessages(int status, string code, string expectedKey) =>
        Assert.AreEqual(expectedKey, ReferralRules.ClaimErrorKey(status, code));

    [TestMethod]
    [DataRow(400, "self_referral", "Referrals_Attribute_SelfReferral")]
    [DataRow(409, "already_attributed", "Referrals_Attribute_Conflict")]
    [DataRow(409, "attribution_window_closed", "Referrals_Attribute_PastWindow")]
    [DataRow(404, "unknown_code", "Referrals_Attribute_Invalid")]
    [DataRow(400, "invalid_code", "Referrals_Attribute_Invalid")]
    [DataRow(429, "rate_limited", "Referrals_Error_RateLimited")]
    [DataRow(400, "invalid_body", "Referrals_Attribute_Rejected")]
    public void AttributionErrors_MapToReadableMessages(int status, string code, string expectedKey) =>
        Assert.AreEqual(expectedKey, ReferralRules.AttributionErrorKey(status, code));

    [TestMethod]
    [DataRow(1, "Referrals_Invitee_DaysLeft_One_Format")]
    [DataRow(0, "Referrals_Invitee_DaysLeft_Format")]
    [DataRow(2, "Referrals_Invitee_DaysLeft_Format")]
    [DataRow(21, "Referrals_Invitee_DaysLeft_Format")]
    public void CountedStrings_UseTheSingularKeyForOne(int count, string expected) =>
        Assert.AreEqual(expected, ReferralRules.CountKey("Referrals_Invitee_DaysLeft_Format", count));

    [TestMethod]
    public void EveryKeyTheRulesReturn_ExistsInEnglishAndPolish()
    {
        string[] keys =
        [
            .. new[] { 400, 404, 409, 418, 429 }.SelectMany(s => new[]
            {
                ReferralRules.ClaimErrorKey(s, "invalid_hoyolab_uid"), ReferralRules.ClaimErrorKey(s, "invalid_hoyolab_region"),
                ReferralRules.ClaimErrorKey(s, "invalid_email"), ReferralRules.ClaimErrorKey(s, null),
                ReferralRules.AttributionErrorKey(s, "self_referral"), ReferralRules.AttributionErrorKey(s, "attribution_window_closed"),
                ReferralRules.AttributionErrorKey(s, "unknown_code"), ReferralRules.AttributionErrorKey(s, null),
            }),
            ReferralRules.CountKey("Referrals_Invitee_DaysLeft_Format", 1),
            ReferralRules.CountKey("Referrals_Invitee_Summary_Pending_Format", 1),
            ReferralRules.CountKey("Referrals_Invitee_Summary_Active_Format", 1),
            ReferralRules.CountKey("Referrals_Invitee_Summary_Flagged_Format", 1),
        ];

        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Application).TypeHandle);
        string assembly = typeof(GameRegionProfile).Assembly.GetName().Name;
        foreach (string culture in new[] { "en-US", "pl-PL" })
        {
            var dict = new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/{assembly};component/Resources/Strings.{culture}.xaml", UriKind.Absolute),
            };
            foreach (string key in keys.Distinct())
                Assert.IsFalse(string.IsNullOrWhiteSpace(dict[key] as string), $"Strings.{culture}.xaml has no \"{key}\".");
        }
    }
}
