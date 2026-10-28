using System.Runtime.Versioning;
using System.Security.Cryptography;
using AgentSpeed.Linux.Rendering;
using AgentSpeed.Linux.UI;
using AgentSpeed.Linux.Platform;
using AgentSpeed.Presentation.Colors;
using AgentSpeed.Presentation.Motion;
using AgentSpeed.Presentation.Orb;
using AgentSpeed.Presentation.Orb.Gpu;
using AgentSpeed.Presentation.Settings;
using AgentSpeed.Reporting;
using Avalonia.Media;
using SkiaSharp;

[assembly: SupportedOSPlatform("linux")]
namespace AgentSpeed.Linux.Tests;

public sealed class PlatformTests
{
    [Theory]
    [InlineData("'prefer-dark'", "'Yaru'", true)]
    [InlineData("'prefer-light'", "'Yaru-dark'", false)]
    [InlineData("'default'", "'Yaru-dark'", true)]
    [InlineData("'default'", "'Adwaita'", false)]
    public void AutomaticOrbAppearanceFollowsGnomePreference(string scheme, string theme, bool dark)
        => Assert.Equal(dark, LinuxAccessibility.ResolveDark(scheme, theme));

    [Fact]
    public void OrbDigitsUseTheShippedFontInsteadOfAPlatformFallback()
    {
        using var glyphs = new SkiaGlyphs();
        foreach (var (text, font) in new[] { ("0", OrbFont.Value), ("183", OrbFont.Value), ("tok/s", OrbFont.Unit) })
        {
            Assert.True(glyphs.TryGet(OrbTextKey.Create(text, font, 36), out var glyph));
            Assert.Contains("Inter", glyphs.At(glyph.U0).Face.FamilyName, StringComparison.OrdinalIgnoreCase);
            Assert.True(glyph.Advance > 10);
            using var renderer = new SkiaOrbRenderer(glyphs);
            var list = new OrbDrawList(); list.AddText(glyph, 30, 70, Rgba.White);
            using var surface = SKSurface.Create(new SKImageInfo(200, 100));
            surface.Canvas.Clear(SKColors.Transparent); renderer.Draw(surface.Canvas, list, software: true);
            using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
            Assert.True(pixels.Pixels.Count(p => p.Alpha > 128) > 50, $"{text} contains no legible ink.");
        }
    }
    [Fact]
    public void ChartScaleHandlesEmptyZeroAndNonFiniteSamples()
    {
        Assert.Equal(1, LineChart.SafeScaleMax([]));
        Assert.Equal(1, LineChart.SafeScaleMax([0, -2, double.NaN, double.PositiveInfinity]));
        var clean = LineChart.Sanitize([double.NaN, -2, 12.5, double.PositiveInfinity]);
        Assert.Equal([0d, 0d, 12.5, 0d], clean);
        Assert.True(LineChart.SafeScaleMax(clean) >= 12.5);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void DesktopPaletteKeepsTextReadable(bool dark, bool highContrast)
    {
        var palette = DesktopStyles.PaletteFor(dark, highContrast);
        foreach (var background in new[] { palette.Window, palette.Sidebar, palette.Card, palette.CardInset })
        {
            Assert.True(Contrast(palette.TextPrimary, background) >= 4.5);
            Assert.True(Contrast(palette.TextSecondary, background) >= 4.5);
            Assert.True(Contrast(palette.TextMuted, background) >= 4.5);
            Assert.True(Contrast(palette.Success, background) >= 4.5);
            Assert.True(Contrast(palette.Accent, background) >= 4.5);
        }
    }

    private static double Contrast(Color foreground, Color background) =>
        Rgba.ContrastRatio(ToRgba(foreground), ToRgba(background));

    private static Rgba ToRgba(Color color) => new(color.R / 255.0, color.G / 255.0, color.B / 255.0, color.A / 255.0);

    [Fact]
    public void LinuxUiHasAllFourLanguages()
    {
        Assert.Empty(AgentSpeed.Linux.UI.LinuxText.Table.MissingTranslations(AgentSpeed.Presentation.Localization.LanguageCodes.All));
        Assert.Equal("实时面板", AgentSpeed.Linux.UI.LinuxText.Get("zh-Hans", "Dashboard"));
        Assert.Equal("ダッシュボード", AgentSpeed.Linux.UI.LinuxText.Get("ja", "Dashboard"));
    }
    [Theory]
    [InlineData(OrbStyle.Led)]
    [InlineData(OrbStyle.Needle)]
    [InlineData(OrbStyle.Liquid)]
    [InlineData(OrbStyle.Reactor)]
    [InlineData(OrbStyle.Minimal)]
    public void AllStylesRenderAtIdleAndRedline(OrbStyle style)
    {
        using var glyphs = new SkiaGlyphs(); using var renderer = new SkiaOrbRenderer(glyphs);
        var instrument = new OrbGpuOrb(42, glyphs);
        var effects = new EffectsPolicy(EffectsIntensity.Standard, true);
        var labels = Enumerable.Repeat("TEST", 16).ToArray();
        instrument.Configure(new(style, 148, effects, true, true, 100, new("tok/s", "NEW PEAK", labels, true), null, default),
            new(148, 1, OrbTokens.Dark, effects));
        using var surface = SKSurface.Create(new SKImageInfo(222, 222));
        foreach (var speed in new[] { 0, 140d })
        {
            instrument.SetData(speed + 1, speed, AgentSpeed.Presentation.Heat.SpeedTier.Boost,
                AgentSpeed.Presentation.Heat.SpeedTitle.Rocket, [], "", null);
            instrument.Render(speed + 1);
            foreach (var software in new[] { false, true })
            {
                surface.Canvas.Clear(SKColors.Transparent);
                renderer.Draw(surface.Canvas, instrument.List, software);
                using var image = surface.Snapshot(); using var pixels = SKBitmap.FromImage(image);
                Assert.Contains(pixels.Pixels, p => p.Alpha > 0);
                Assert.Equal(0, pixels.GetPixel(0, 0).Alpha); // The floating window must retain its transparent corners.
            }
        }
    }
    [Fact]
    public void DeviceSecretsArePrivateAndSurviveRotation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-speed-secrets-" + Guid.NewGuid());
        const string id = "123e4567-e89b-42d3-a456-426614174000";
        try
        {
            var keys = new ReportKeyStore(directory);
            using var old = keys.CreateKey(id); using var loaded = keys.LoadKey(id);
            Assert.Equal(old.PublicKey.Fingerprint, loaded!.PublicKey.Fingerprint);
            using var next = keys.CreateKey(id, pending: true); Assert.True(keys.PromotePending(id));
            using var rotated = keys.LoadKey(id); Assert.Equal(next.PublicKey.Fingerprint, rotated!.PublicKey.Fingerprint);
            keys.SaveApiKey("secret", id); Assert.Equal("secret", keys.LoadApiKey(id));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
            foreach (var file in Directory.EnumerateFiles(directory)) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.Throws<ArgumentException>(() => keys.CreateKey("../../outside"));
            keys.DeleteAll(id); Assert.False(keys.HasFiles(id));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void KeyStoreWriteFailuresUseTheHandledStorageExceptionContract()
    {
        var root = Path.Combine(Path.GetTempPath(), "agent-speed-secret-blocker-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        const string id = "123e4567-e89b-42d3-a456-426614174000";
        try
        {
            var blockedDirectory = Path.Combine(root, "report");
            File.WriteAllText(blockedDirectory, "not a directory");
            var keys = new ReportKeyStore(blockedDirectory);

            var createError = Assert.Throws<CryptographicException>(() => keys.CreateKey(id));
            Assert.IsAssignableFrom<IOException>(createError.InnerException);
            var apiKeyError = Assert.Throws<CryptographicException>(() => keys.SaveApiKey("fake-api-key-123", id));
            Assert.IsAssignableFrom<IOException>(apiKeyError.InnerException);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public void FailedAtomicReplacementPreservesTheExistingDeviceKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-speed-secret-replacement-" + Guid.NewGuid());
        const string id = "123e4567-e89b-42d3-a456-426614174000";
        try
        {
            var keys = new ReportKeyStore(directory);
            using var original = keys.CreateKey(id);
            var blockedPendingPath = Path.Combine(directory, id + ".pending.key");
            Directory.CreateDirectory(blockedPendingPath);

            Assert.Throws<CryptographicException>(() => keys.CreateKey(id, pending: true));
            using var loaded = keys.LoadKey(id);
            Assert.Equal(original.PublicKey.Fingerprint, loaded!.PublicKey.Fingerprint);
            Assert.DoesNotContain(Directory.EnumerateFiles(directory),
                path => Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void FailedPendingPromotionKeepsThePendingKeyAndDeleteIsBestEffort()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-speed-secret-promotion-" + Guid.NewGuid());
        const string id = "123e4567-e89b-42d3-a456-426614174000";
        try
        {
            var keys = new ReportKeyStore(directory);
            using var pending = keys.CreateKey(id, pending: true);
            var blockedTarget = Path.Combine(directory, id + ".key");
            Directory.CreateDirectory(blockedTarget);

            Assert.False(keys.PromotePending(id));
            Assert.True(File.Exists(Path.Combine(directory, id + ".pending.key")));
            keys.DeleteAll(id);
            Assert.True(Directory.Exists(blockedTarget));
            Assert.False(File.Exists(Path.Combine(directory, id + ".pending.key")));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void RelativeXdgPathIsIgnored()
    {
        const string variable = "AGENT_SPEED_XDG_UNIT_TEST";
        var before = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "relative/path");
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "agent-speed"), LinuxPaths.Resolve(variable, ".config"));
        }
        finally { Environment.SetEnvironmentVariable(variable, before); }
    }
}
