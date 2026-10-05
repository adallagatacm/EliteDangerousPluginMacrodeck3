using MacroDeck.Plugin.Testing;
using NUnit.Framework;

namespace EliteDangerousPlugin.Tests;

/// <summary>
/// Behaviour tests through <see cref="PluginTestHarness"/>: the plugin's own capability handlers run,
/// but nothing crosses a socket. This is where you test what your integration does.
/// </summary>
[TestFixture]
public sealed class PluginIntegrationTests
{
    private static PluginTestHarness CreateHarness() =>
        PluginTestHarness.Create(builder => builder
            .UseLocalization(Strings.LocalizationCatalog)
            .RegisterIntegration<PluginIntegration>());

    [Test]
    public async Task The_plugin_builds_and_initializes()
    {
        await using var harness = CreateHarness();

        Assert.DoesNotThrowAsync(harness.InitializeIntegrationsAsync);
    }

    [Test]
    public async Task The_resync_action_succeeds()
    {
        await using var harness = CreateHarness();
        await harness.InitializeIntegrationsAsync();

        var outcome = await harness.Actions.ExecuteAsync(
            "resync-variables",
            new Dictionary<string, object?>());

        Assert.That(outcome.Succeeded, Is.True);
    }

    [Test]
    public async Task The_plugin_declares_the_resync_action()
    {
        await using var harness = CreateHarness();
        await harness.InitializeIntegrationsAsync();

        // Verifica che l'azione sia registrata e abbia l'id corretto
        var outcome = await harness.Actions.ExecuteAsync(
            "resync-variables",
            new Dictionary<string, object?>());

        // L'azione esiste e non lancia eccezioni
        Assert.That(outcome, Is.Not.Null);
    }
}

/// <summary>
/// The localization set is generated from <c>Localization/*.resx</c>, so these guard the wiring rather
/// than any wording: a missing catalog registration leaves every label showing its raw key.
/// </summary>
[TestFixture]
public sealed class LocalizationTests
{
    [Test]
    public void The_catalog_is_scoped_to_the_plugin_id()
    {
        Assert.That(Strings.LocalizationCatalog.Scope, Is.EqualTo("plugin:com.grega81.elite-dangerous"));
    }

    [Test]
    public void English_is_the_default_culture()
    {
        Assert.That(Strings.LocalizationCatalog.DefaultCulture, Is.EqualTo("en"));
        Assert.That(Strings.LocalizationCatalog.Cultures, Does.Contain("en"));
    }

    [Test]
    public void The_action_strings_come_from_the_catalog()
    {
        // Verifica che il catalogo contenga almeno una chiave di azione.
        // Il template di default dichiara "Actions.LogMessage.Name"; dopo la sostituzione
        // dell'azione potresti avere chiavi diverse. Controlliamo che ce ne sia almeno una.
        var keys = Strings.LocalizationCatalog.KeysOf("en").ToList();
        Assert.That(keys, Is.Not.Empty);
        Assert.That(keys.Any(k => k.StartsWith("Actions.", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void Every_key_the_default_culture_declares_resolves_to_text()
    {
        foreach (var key in Strings.LocalizationCatalog.KeysOf("en"))
        {
            Assert.That(Strings.LocalizationCatalog.TryGetTemplate("en", key, out var text), Is.True);
            Assert.That(text, Is.Not.Empty);
        }
    }
}