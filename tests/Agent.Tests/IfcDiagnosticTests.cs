using RhinoMagpie.Agent;

static class IfcDiagnosticTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("FAIL " + name);
            Console.WriteLine("PASS " + name);
            passed++;
        }
        foreach (string code in new[] { "runtime_missing", "runtime_version" })
        {
            string message = IfcRuntimeDiagnostics.DescribeWorkerFailure(
                "{\"error\":{\"code\":\"" + code + "\",\"message\":\"Run pip install or configure Python\"}}", "");
            Check(message.Contains("完整") && message.Contains("重新安装"), code + " gives complete-bundle guidance");
            Check(!message.Contains("pip", StringComparison.OrdinalIgnoreCase) &&
                !message.Contains("configure", StringComparison.OrdinalIgnoreCase), code + " never suggests manual dependency setup");
        }
        Check(IfcRuntimeDiagnostics.DescribeWorkerFailure(
            "{\"error\":{\"code\":\"output_exists\",\"message\":\"Choose a new filename\"}}", "")
                .Contains("output_exists: Choose a new filename"), "normal worker diagnostics remain actionable");
        Check(IfcRuntimeDiagnostics.DescribeWorkerFailure("not json", "native error") == "native error",
            "malformed worker output preserves native stderr");
        Check(IfcRuntimeDiagnostics.DescribeWorkerFailure("not json", new string('x', 3000)).Length <= 1600,
            "worker diagnostics stay bounded");
        return passed;
    }
}
