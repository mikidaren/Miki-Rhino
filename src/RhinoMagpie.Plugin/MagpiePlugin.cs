using System.Runtime.InteropServices;
using Rhino;
using Rhino.Commands;
using Rhino.PlugIns;
using Rhino.UI;
using RhinoMagpie.Agent;

[assembly: Guid("20e04364-aa79-4c4c-9d51-830af0ec16c7")]
[assembly: PlugInDescription(DescriptionType.Organization, "mikidaren")]

namespace RhinoMagpie.Plugin;

public sealed class MagpiePlugin : PlugIn
{
    internal static MagpiePlugin? Instance { get; private set; }
    internal NativeAiIntegration? NativeAi { get; private set; }
    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        try
        {
            MagpieGateway.EnsureConfigurationDirectory();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            errorMessage = "无法创建 Miki-Rhino 连接目录，请检查用户目录的写入权限。";
            return LoadReturnCode.ErrorShowDialog;
        }
        Panels.RegisterPanel(this, typeof(MagpiePanel), "Miki-Rhino", null);
        Instance = this;
        NativeAi = new NativeAiIntegration();
        return LoadReturnCode.Success;
    }

    protected override void OnShutdown()
    {
        NativeAi?.Dispose();
        NativeAi = null;
        Instance = null;
        base.OnShutdown();
    }
}

[Guid("c56b3e5c-fd1e-4a91-995d-0b5f77bf01e6")]
public sealed class MagpieCommand : Command
{
    public override string EnglishName => "MikiRhino";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        if (MagpiePlugin.Instance?.NativeAi?.OpenMagpie() == true) return Result.Success;
        if (!Panels.IsPanelVisible(typeof(MagpiePanel)) &&
            Panels.OpenPanelAsSibling(typeof(MagpiePanel).GUID, PanelIds.Layers, true))
            return Result.Success;
        Panels.OpenPanel(typeof(MagpiePanel).GUID);
        return Result.Success;
    }
}

[Guid("309a7845-50ba-41e4-aaa2-66faa23e38a4")]
public sealed class MagpieStandaloneCommand : Command
{
    public override string EnglishName => "MikiRhinoStandalone";

    protected override Result RunCommand(RhinoDoc doc, RunMode mode)
    {
        Panels.OpenPanel(typeof(MagpiePanel).GUID);
        return Result.Success;
    }
}
