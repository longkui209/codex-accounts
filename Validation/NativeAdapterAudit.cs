using System.Text.Json;
using CodexAccountSwitcher.ModelRouting;

namespace CodexAccountSwitcher.Validation;

/// <summary>Read-only installation/structure check. Does not launch or connect a desktop.</summary>
internal static class NativeAdapterAudit
{
    internal static void Run(string destination)
    {
        var path=Path.GetFullPath(destination);
        if(!string.Equals(Path.GetDirectoryName(path),ValidationPaths.OutputDirectory,StringComparison.OrdinalIgnoreCase)||File.Exists(path))throw new IOException();
        var stage="installed-source";
        try
        {
            var catalog=NativeAdapterCatalog.Installed(value=>stage=value);
            var rows=catalog.Assets.Select(asset=>
            {
                if(!catalog.TryPlan(asset,out var plan)||plan is null)throw new IOException();
                return new{asset,hash=plan.SourceHash,work=plan.Work,factory=plan.Factory,store=plan.Store,signalInitialization=plan.SignalInitialization,
                    derived=plan.Derived,nativeAtom=plan.NativeAtom,workCallback=plan.Work?plan.WorkCallback:null,
                    importedSignalExport=plan.ImportedSignalExport,importedDerivedExport=plan.ImportedDerivedExport,structureValidated=true};
            }).ToArray();
            File.WriteAllText(path,JsonSerializer.Serialize(new{passed=rows.Length==2,installationReadOnly=true,desktopLaunched=false,accountDataRead=false,runtimePrelude=NativeSendAdapter.RuntimePrelude,plans=rows}));
        }
        catch(Exception error)
        {
            File.WriteAllText(path,JsonSerializer.Serialize(new{passed=false,stage,errorType=error.GetType().Name,accountDataRead=false}));throw;
        }
    }
}
