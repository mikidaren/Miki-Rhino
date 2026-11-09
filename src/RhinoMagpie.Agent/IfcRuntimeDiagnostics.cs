using System.Text.Json.Nodes;

namespace RhinoMagpie.Agent;

public static class IfcRuntimeDiagnostics
{
    public static string DescribeWorkerFailure(string stdout, string stderr)
    {
        string message = stderr;
        try
        {
            var diagnostic = JsonNode.Parse(stdout)?["error"];
            if (diagnostic is not null)
            {
                string? code = diagnostic["code"]?.GetValue<string>();
                if (code is "runtime_missing" or "runtime_version")
                    return "内置 IFC 组件无法加载或版本不匹配，请重新安装完整离线插件包，无需自行配置依赖。";
                message = code + ": " + diagnostic["message"]?.GetValue<string>();
            }
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException) { }
        return message[..Math.Min(message.Length, 1600)];
    }
}
