using System.CommandLine;
using System.CommandLine.Help;
using TinyWin2.Cli.Commands;
using TinyWin2.Core.Pipeline;

namespace TinyWin2.Cli;

internal static class CommandLine {
    public static RootCommand CreateRootCommand() {
        var root = new RootCommand("layered Windows image slimming with explicit inputs, outputs, and workspaces");
        var help = new HelpAction();
        root.SetAction(_ => help.Invoke(root.Parse(["--help"])));
        root.Add(Doctor());
        root.Add(Inspect());
        root.Add(Plans());
        root.Add(Profiles());
        root.Add(Validate());
        root.Add(Build());
        root.Add(Preview());
        root.Add(Package());
        root.Add(Layers());
        return root;
    }

    private static Command Doctor() {
        var command = new Command("doctor", "check the local environment for a build workspace");
        var workspace = RequiredText("--workspace", "local workspace path used for disk-space checks");
        var json = Flag("--json", "write machine-readable JSON");
        command.Add(workspace);
        command.Add(json);
        command.SetAction(result => DoctorHandler.Execute(new(
            Path.GetFullPath(result.GetRequiredValue(workspace)),
            result.GetValue(json))));
        return command;
    }

    private static Command Inspect() {
        var command = new Command("inspect", "list image indexes in an ISO, media folder, WIM, or ESD");
        var input = RequiredText("--input", "ISO, media folder, WIM, or ESD file");
        var json = Flag("--json", "write machine-readable JSON");
        command.Add(input);
        command.Add(json);
        command.SetAction(result => InspectHandler.ExecuteAsync(new(
            result.GetRequiredValue(input),
            result.GetValue(json))));
        return command;
    }

    private static Command Plans() {
        var command = new Command("plan", "explore the plan catalog");

        var list = new Command("list", "list plans grouped by category");
        var listPlansDirectory = Text("--plans-dir", "directory containing plan JSON files");
        var category = Text("--category", "only show plans in this category");
        var listJson = Flag("--json", "write machine-readable JSON");
        list.Add(listPlansDirectory);
        list.Add(category);
        list.Add(listJson);
        list.SetAction(result => PlanHandler.List(new(
            result.GetValue(listPlansDirectory),
            result.GetValue(category),
            result.GetValue(listJson))));

        var show = new Command("show", "show the complete definition of one plan");
        var id = Argument<string>("id", "plan identifier");
        var showPlansDirectory = Text("--plans-dir", "directory containing plan JSON files");
        show.Add(id);
        show.Add(showPlansDirectory);
        show.SetAction(result => PlanHandler.Show(new(
            result.GetRequiredValue(id),
            result.GetValue(showPlansDirectory))));

        command.Add(list);
        command.Add(show);
        return command;
    }

    private static Command Profiles() {
        var command = new Command("profile", "list, inspect, export, or validate selection profiles");

        var list = new Command("list", "list profiles in the profiles directory");
        var listPlansDirectory = Text("--plans-dir", "directory containing plan JSON files");
        var listProfilesDirectory = Text("--profiles-dir", "profiles directory");
        list.Add(listPlansDirectory);
        list.Add(listProfilesDirectory);
        list.SetAction(result => ProfileHandler.List(new(
            result.GetValue(listPlansDirectory),
            result.GetValue(listProfilesDirectory))));

        var show = new Command("show", "print a profile as JSON");
        var showFile = RequiredText("--input", "profile JSON file");
        show.Add(showFile);
        show.SetAction(result => ProfileHandler.Show(new(result.GetRequiredValue(showFile))));

        var export = new Command("export", "write a profile from selected plans");
        var exportSelection = AddSelectionOptions(export);
        var exportOutput = RequiredText("--output", "destination profile JSON file");
        var exportName = Text("--name", "profile display name");
        export.Add(exportOutput);
        export.Add(exportName);
        export.SetAction(result => ProfileHandler.Export(new(
            result.GetRequiredValue(exportOutput),
            result.GetValue(exportName),
            ReadSelection(result, exportSelection))));

        var validate = new Command("validate", "validate a profile against the current plan catalog");
        var validateFile = RequiredText("--input", "profile JSON file");
        var validatePlansDirectory = Text("--plans-dir", "directory containing plan JSON files");
        validate.Add(validateFile);
        validate.Add(validatePlansDirectory);
        validate.SetAction(result => ProfileHandler.Validate(new(
            result.GetRequiredValue(validateFile),
            result.GetValue(validatePlansDirectory))));

        command.Add(list);
        command.Add(show);
        command.Add(export);
        command.Add(validate);
        return command;
    }

    private static Command Validate() {
        var command = new Command("validate", "validate a built image or bootable media input");
        var input = RequiredText("--input", "image, media folder, or ISO to validate");
        var kind = RequiredText("--kind", "validation kind: image, media, or iso");
        kind.AcceptOnlyFromAmong("image", "media", "iso");
        var json = Flag("--json", "write machine-readable JSON");
        command.Add(input);
        command.Add(kind);
        command.Add(json);
        command.SetAction(result => ValidateHandler.ExecuteAsync(new(
            result.GetRequiredValue(input),
            result.GetRequiredValue(kind),
            result.GetValue(json))));
        return command;
    }

    private static Command Build() {
        var command = new Command("build", "build one explicit WIM, ESD, or debug VHDX artifact");
        var input = RequiredText("--input", "source ISO, media folder, WIM, or ESD file");
        var index = Required<int>("--index", "source image index");
        var selection = AddSelectionOptions(command);
        var output = RequiredText("--output", "destination .wim, .esd, or .vhdx file");
        var workspace = RequiredText("--workspace", "persistent layer workspace directory");
        var format = RequiredText("--format", "output format");
        format.AcceptOnlyFromAmong("wim", "esd", "vhdx");
        var export = AddExportOptions(command);
        var continueOnError = Flag("--continue-on-error",
            "continue after a failed plan and mark the artifact incomplete");
        var dryRun = Flag("--dry-run", "resolve and validate without applying plans");
        var singleLayer = Flag("--single-layer", "debug mode: apply all plans in one non-atomic mount");
        var skipEvidence = Flag("--skip-evidence", "skip per-layer evidence capture");
        var resume = Flag("--resume", "reuse the existing layer workspace");
        var overwrite = Flag("--overwrite", "replace an existing output file");
        var baseSize = Number("--base-vhdx-mb", "maximum dynamic base VHDX size in MB");
        baseSize.DefaultValueFactory = _ => BuildOptions.DefaultBaseVhdxMaximumMb;
        var jsonEvents = Flag("--json-events", "write JSONL build events to stdout");

        command.Add(input);
        command.Add(index);
        command.Add(output);
        command.Add(workspace);
        command.Add(format);
        command.Add(continueOnError);
        command.Add(dryRun);
        command.Add(singleLayer);
        command.Add(skipEvidence);
        command.Add(resume);
        command.Add(overwrite);
        command.Add(baseSize);
        command.Add(jsonEvents);
        command.SetAction(result => BuildHandler.ExecuteAsync(new(
            result.GetRequiredValue(input),
            result.GetRequiredValue(index),
            ReadSelection(result, selection),
            result.GetRequiredValue(output),
            result.GetRequiredValue(workspace),
            Cli.ParseOutputFormat(result.GetRequiredValue(format)),
            result.GetValue(export.Fast),
            result.GetValue(export.Compression),
            result.GetValue(export.Verify),
            result.GetValue(export.NoVerify),
            result.GetValue(export.CheckIntegrity),
            result.GetValue(continueOnError),
            result.GetValue(dryRun),
            result.GetValue(singleLayer),
            result.GetValue(skipEvidence),
            result.GetValue(resume),
            result.GetValue(overwrite),
            result.GetValue(baseSize),
            result.GetValue(jsonEvents))));
        return command;
    }

    private static Command Preview() {
        var command = new Command("preview", "report what each plan would change in an explicit workspace");
        var input = RequiredText("--input", "source ISO, media folder, WIM, or ESD file");
        var index = Required<int>("--index", "source image index");
        var selection = AddSelectionOptions(command);
        var workspace = RequiredText("--workspace", "preview workspace directory");
        var baseSize = Number("--base-vhdx-mb", "maximum dynamic base VHDX size in MB");
        baseSize.DefaultValueFactory = _ => BuildOptions.DefaultBaseVhdxMaximumMb;
        var json = Flag("--json", "write machine-readable JSON");
        command.Add(input);
        command.Add(index);
        command.Add(workspace);
        command.Add(baseSize);
        command.Add(json);
        command.SetAction(result => PreviewHandler.ExecuteAsync(new(
            result.GetRequiredValue(input),
            result.GetRequiredValue(index),
            ReadSelection(result, selection),
            result.GetRequiredValue(workspace),
            result.GetValue(baseSize),
            result.GetValue(json))));
        return command;
    }

    private static Command Package() {
        var command = new Command("package", "package an explicit media input into a final artifact");
        var iso = new Command("iso", "create a bootable BIOS+UEFI ISO from media and a built image");
        var input = RequiredText("--input", "source ISO or extracted media folder");
        var image = RequiredText("--image", "built install.wim or install.esd");
        var output = RequiredText("--output", "destination .iso file");
        var workspace = RequiredText("--workspace", "temporary media staging directory");
        var oscdimg = RequiredText("--oscdimg", "path to oscdimg.exe");
        var overwrite = Flag("--overwrite", "replace an existing ISO");
        iso.Add(input);
        iso.Add(image);
        iso.Add(output);
        iso.Add(workspace);
        iso.Add(oscdimg);
        iso.Add(overwrite);
        iso.SetAction(result => PackageHandler.CreateIsoAsync(new(
            result.GetRequiredValue(input),
            result.GetRequiredValue(image),
            result.GetRequiredValue(output),
            result.GetRequiredValue(workspace),
            result.GetRequiredValue(oscdimg),
            result.GetValue(overwrite))));
        command.Add(iso);
        return command;
    }

    private static Command Layers() {
        var command = new Command("layer", "inspect, compare, extract, or capture layer states");

        var list = new Command("list", "list the layer chain in a workspace");
        var listWorkspace = RequiredText("--workspace", "build workspace directory");
        var listJson = Flag("--json", "write machine-readable JSON");
        list.Add(listWorkspace);
        list.Add(listJson);
        list.SetAction(result => LayerHandler.List(new(
            result.GetRequiredValue(listWorkspace),
            result.GetValue(listJson))));

        var diff = new Command("diff", "show file and registry changes between two layers");
        var diffWorkspace = RequiredText("--workspace", "build workspace directory");
        var from = Required<int>("--from", "starting layer index");
        var to = Required<int>("--to", "ending layer index");
        var diffJson = Flag("--json", "write machine-readable JSON");
        diff.Add(diffWorkspace);
        diff.Add(from);
        diff.Add(to);
        diff.Add(diffJson);
        diff.SetAction(result => LayerHandler.Diff(new(
            result.GetRequiredValue(diffWorkspace),
            result.GetRequiredValue(from),
            result.GetRequiredValue(to),
            result.GetValue(diffJson))));

        var extract = new Command("extract", "extract an image-relative file from a layer");
        var extractWorkspace = RequiredText("--workspace", "build workspace directory");
        var extractLayer = Required<int>("--layer", "layer index");
        var imagePath = RequiredText("--image-path", "path relative to the image root");
        var destination = RequiredText("--output", "destination file");
        extract.Add(extractWorkspace);
        extract.Add(extractLayer);
        extract.Add(imagePath);
        extract.Add(destination);
        extract.SetAction(result => LayerHandler.Extract(new(
            result.GetRequiredValue(extractWorkspace),
            result.GetRequiredValue(extractLayer),
            result.GetRequiredValue(imagePath),
            result.GetRequiredValue(destination))));

        var rollback = new Command("rollback", "capture a layer state as a WIM or ESD");
        var rollbackWorkspace = RequiredText("--workspace", "build workspace directory");
        var rollbackLayer = Required<int>("--layer", "layer index");
        var rollbackOutput = RequiredText("--output", "destination .wim or .esd file");
        var rollbackFormat = RequiredText("--format", "capture format");
        rollbackFormat.AcceptOnlyFromAmong("wim", "esd");
        var rollbackExport = AddExportOptions(rollback);
        rollback.Add(rollbackWorkspace);
        rollback.Add(rollbackLayer);
        rollback.Add(rollbackOutput);
        rollback.Add(rollbackFormat);
        rollback.SetAction(result => LayerHandler.Rollback(new(
            result.GetRequiredValue(rollbackWorkspace),
            result.GetRequiredValue(rollbackLayer),
            result.GetRequiredValue(rollbackOutput),
            Cli.ParseCaptureFormat(result.GetRequiredValue(rollbackFormat)),
            result.GetValue(rollbackExport.Fast),
            result.GetValue(rollbackExport.Compression),
            result.GetValue(rollbackExport.Verify),
            result.GetValue(rollbackExport.NoVerify),
            result.GetValue(rollbackExport.CheckIntegrity))));

        command.Add(list);
        command.Add(diff);
        command.Add(extract);
        command.Add(rollback);
        return command;
    }

    private static SelectionSymbols AddSelectionOptions(Command command) {
        var plansDirectory = Text("--plans-dir", "directory containing plan JSON files");
        var profiles = Multiple("--profile", "profile JSON file; may be repeated");
        var plans = Multiple("--plan", "plan identifier; may be repeated");
        var sets = Multiple("--set", "plan.parameter=value; may be repeated");
        command.Add(plansDirectory);
        command.Add(profiles);
        command.Add(plans);
        command.Add(sets);
        return new(plansDirectory, profiles, plans, sets);
    }

    private static ExportSymbols AddExportOptions(Command command) {
        var fast = Flag("--fast", "skip per-layer health checks and use fast capture defaults");
        var compression = Text("--compression",
            "WIM/source-ESD compression: none, fast, or max; ESD final output remains recovery");
        compression.AcceptOnlyFromAmong("none", "fast", "max");
        var verify = Flag("--verify", "verify captured WIM data");
        var noVerify = Flag("--no-verify", "skip verification of captured WIM data");
        var checkIntegrity = Flag("--check-integrity", "ask DISM to validate source and exported image integrity");
        command.Add(fast);
        command.Add(compression);
        command.Add(verify);
        command.Add(noVerify);
        command.Add(checkIntegrity);
        return new(fast, compression, verify, noVerify, checkIntegrity);
    }

    private static SelectionRequest ReadSelection(ParseResult result, SelectionSymbols symbols) => new(
        result.GetValue(symbols.PlansDirectory),
        result.GetValue(symbols.ProfilesOption) ?? [],
        result.GetValue(symbols.PlansOption) ?? [],
        result.GetValue(symbols.Sets) ?? []);

    private static Option<string> RequiredText(string name, string description) =>
        new(name) { Description = description, Required = true };

    private static Option<T> Required<T>(string name, string description) =>
        new(name) { Description = description, Required = true };

    private static Option<string?> Text(string name, string description) =>
        new(name) { Description = description };

    private static Option<long> Number(string name, string description) =>
        new(name) { Description = description };

    private static Option<string[]> Multiple(string name, string description) =>
        new(name) {
            Description = description,
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false
        };

    private static Option<bool> Flag(string name, string description) =>
        new(name) { Description = description };

    private static Argument<T> Argument<T>(string name, string description) =>
        new(name) {
            Description = description,
            Arity = ArgumentArity.ExactlyOne
        };

    private sealed record SelectionSymbols(
        Option<string?> PlansDirectory,
        Option<string[]> ProfilesOption,
        Option<string[]> PlansOption,
        Option<string[]> Sets);

    private sealed record ExportSymbols(
        Option<bool> Fast,
        Option<string?> Compression,
        Option<bool> Verify,
        Option<bool> NoVerify,
        Option<bool> CheckIntegrity);
}
