using System.Security.Cryptography;
using ReolinkDBWin.Core.Licensing;

namespace ReolinkDBWin.Core.Tests;

public class LicenseTests
{
    private static readonly ECDsa Private = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly string Public = Convert.ToBase64String(Private.ExportSubjectPublicKeyInfo());

    private static string Issue(DateOnly expires, ECDsa? key = null) => LicenseKey.Create(new LicenseInfo
    {
        Id = "L-1", Name = "Client Test", Email = "client@example.com", Issued = expires.AddYears(-1), Expires = expires,
    }, key ?? Private);

    [Fact]
    public void Valid_key_round_trips_and_tolerates_whitespace()
    {
        var key = Issue(new DateOnly(2027, 6, 1));
        using var pub = LicenseKey.ImportPublicKey(Public);
        var spaced = key.Insert(20, "\n  ").Insert(5, " ");
        Assert.True(LicenseKey.TryVerify(spaced, pub, out var info, out _));
        Assert.Equal("Client Test", info!.Name);
        Assert.Equal(new DateOnly(2027, 6, 1), info.Expires);
    }

    [Fact]
    public void Tampered_or_foreign_key_is_rejected()
    {
        var key = Issue(new DateOnly(2027, 6, 1));
        using var pub = LicenseKey.ImportPublicKey(Public);
        var tampered = key[..10] + (key[10] == 'A' ? 'B' : 'A') + key[11..];
        Assert.False(LicenseKey.TryVerify(tampered, pub, out _, out _));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(LicenseKey.TryVerify(Issue(new DateOnly(2027, 6, 1), other), pub, out _, out var error));
        Assert.Contains("signature", error);
        Assert.False(LicenseKey.TryVerify("n'importe quoi", pub, out _, out _));
    }

    [Fact]
    public void Trial_then_expired()
    {
        var today = new DateOnly(2026, 11, 1);
        var store = new InMemoryLicenseStore();
        var m = new LicenseManager(Public, store, () => today);

        var s = m.Evaluate();
        Assert.Equal(LicenseState.Trial, s.State);
        Assert.Equal(14, s.DaysRemaining);
        Assert.True(s.AllowsUse);

        today = today.AddDays(13);
        Assert.Equal(1, m.Evaluate().DaysRemaining);
        today = today.AddDays(1);
        Assert.Equal(LicenseState.TrialExpired, m.Evaluate().State);
        Assert.False(m.Evaluate().AllowsUse);
    }

    [Fact]
    public void Subscription_lifecycle()
    {
        var today = new DateOnly(2026, 11, 1);
        var store = new InMemoryLicenseStore();
        var m = new LicenseManager(Public, store, () => today);

        var (ok, status, _) = m.Activate(Issue(new DateOnly(2027, 11, 1)));
        Assert.True(ok);
        Assert.Equal(LicenseState.Active, status!.State);
        Assert.NotNull(store.Key);

        today = new DateOnly(2027, 10, 25);
        Assert.Equal(LicenseState.ExpiringSoon, m.Evaluate().State);
        today = new DateOnly(2027, 11, 5);
        var grace = m.Evaluate();
        Assert.Equal(LicenseState.Grace, grace.State);
        Assert.True(grace.AllowsUse);
        today = new DateOnly(2027, 11, 20);
        Assert.Equal(LicenseState.Expired, m.Evaluate().State);
        Assert.False(m.Evaluate().AllowsUse);
    }

    [Fact]
    public void Expired_key_cannot_be_activated_and_dev_build_is_unrestricted()
    {
        var m = new LicenseManager(Public, new InMemoryLicenseStore(), () => new DateOnly(2027, 1, 1));
        var (ok, _, error) = m.Activate(Issue(new DateOnly(2026, 1, 1)));
        Assert.False(ok);
        Assert.Contains("expiré", error);

        var dev = new LicenseManager("", new InMemoryLicenseStore());
        Assert.Equal(LicenseState.DevelopmentBuild, dev.Evaluate().State);
        Assert.True(dev.Evaluate().AllowsUse);
    }
}
