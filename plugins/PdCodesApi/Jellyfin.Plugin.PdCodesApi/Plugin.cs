using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.PdCodesApi.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PdCodesApi;

/// <summary>
/// Plugin entry point.
/// </summary>
/// <remarks>
/// Verified against Jellyfin 10.10.6 (MediaBrowser.Common/Plugins/BasePluginOfT.cs):
/// <c>BasePlugin&lt;TConfigurationType&gt; : BasePlugin, IHasPluginConfiguration</c> with
/// <c>protected BasePlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)</c>,
/// abstract <c>Name</c>, virtual <c>Id</c> and <c>Description</c>.
/// <c>IHasWebPages</c> (MediaBrowser.Model/Plugins/IHasWebPages.cs) declares exactly
/// <c>IEnumerable&lt;PluginPageInfo&gt; GetPages()</c>.
/// </remarks>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Server paths, supplied by the host.</param>
    /// <param name="xmlSerializer">Configuration serializer, supplied by the host.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the singleton instance.
    /// </summary>
    /// <remarks>
    /// Jellyfin constructs exactly one plugin instance and does not expose it to
    /// providers through DI, so a static handle is the sanctioned pattern for reading
    /// configuration from a provider. It is nullable because a provider could in
    /// principle be constructed before the plugin: every consumer must check, and the
    /// providers do — an unconfigured read must fail loudly, not silently use defaults.
    /// </remarks>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "PD-Codes API";

    /// <inheritdoc />
    /// <remarks>
    /// A fixed GUID. It identifies the plugin across upgrades and MUST match the
    /// "guid" in build.yaml and manifest.json — a mismatch makes the repository
    /// offer an update the server never recognizes as the same plugin.
    /// </remarks>
    public override Guid Id => new Guid("3f9c2a17-8d54-4e63-b0a1-5c7de2149f8b");

    /// <inheritdoc />
    public override string Description =>
        "Metadata and images from a self-hosted PD-Codes API (v5) instance.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        // EmbeddedResourcePath must be the full manifest resource name, which MSBuild
        // derives as <RootNamespace>.<folder>.<file> with backslashes turned into dots.
        // Getting this wrong yields a blank settings page and no error anywhere.
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace),
        };
    }
}
