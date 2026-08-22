using System.CommandLine;
using System.CommandLine.Help;
using TinyWin2.Cli.Commands;
using TinyWin2.Core.Pipeline;

namespace TinyWin2.Cli;

internal static class CommandLine {
    public static RootCommand CreateRootCommand() {
        var root = new RootCommand("layered Windows image slimming with resumable VHDX differencing chains");
        var help = new HelpAction();
        root.SetAction(_ => help.Invoke(root.Parse(["--help"])));
        root.Add(Doctor());
        root.Add(Inspect());
        root.Add(Plans());
        root.Add(Profiles());
        root.Add(Build());
        root.Add(Preview());
        root.Add(Layers());
        return root;
    }

    private static Command Doctor() {
        var command = new Command("doctor", "check the local environment and required Windows tools");
        var outputDirectory = Text("--output-dir", "directory used for disk-space checks");
        var json = Flag("--json", "write machine-readable JSON");
        command.Add(outputDirectory);
        command.Add(json);
        command.SetAction(result => DoctorHandler.Execute(new(
            result.GetValue(outputDirectory),
            result.GetValue(json))));
        return command;
    }

    private static Command Inspect() {
        var command = new Command("inspect", "list image indexes in an ISO, WIM, ESD, or folder");
        var source = Argument<string>("source", "ISO, WIM, ESD, or extracted media folder");
        var json = Flag("--json", "write machine-readable JSON");
        command.Add(source);
        command.Add(json);
        command.SetAction(result => InspectHandler.ExecuteAsync(new(
            result.GetRequiredValue(source),
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
        var showFile = Argument<string>("file", "profile JSON file");
        show.Add(showFile);
        show.SetAction(result => ProfileHandler.Show(new(result.GetRequiredValue(showFile))));

        var export = new Command("export", "write a profile from selected plans");
        var exportSelection = AddSelectionOptions(export);
        var exportOutput = RequiredText("--output-file", "destination profile JSON file");
        var exportName = Text("--name", "profile display name");
        export.Add(exportOutput);
        export.Add(exportName);
        export.SetAction(result => ProfileHandler.Export(new(
            result.GetRequiredValue(exportOutput),
            result.GetValue(exportName),
            ReadSelection(result, exportSelection))));

        var validate = new Command("validate", "validate a profile against the current plan catalog");
        var validateFile = Argument<string>("file", "profile JSON file");
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

    private static Command Build() {
        var command = new Command("build", "run a layered slimming build");
        var source = RequiredText("--source", "source ISO, WIM, ESD, or extracted media folder");
        var index = Required<int>("--index", "source image index");
        var selection = AddSelectionOptions(command);
        var outputDirectory = Text("--output-dir", "output root directory");
        outputDirectory.DefaultValueFactory = _ => "out";
        var outputFormat = Text("--output-format", "output format");
        outputFormat.AcceptOnlyFromAmong("wim", "esd", "vhdx");
        outputFormat.DefaultValueFactory = _ => "esd";
        var createIso = Flag("--iso", "package WIM/ESD media as a bootable ISO");
        var baseSize = Number("--base-vhdx-mb", "maximum dynamic base VHDX size in MB");
        baseSize.DefaultValueFactory = _ => BuildOptions.DefaultBaseVhdxMaximumMb;
        var fast = Flag("--fast", "use faster, less conservative capture settings");
        var continueOnError = Flag("--continue-on-error", "continue after a failed plan");
        var keepLayers = Flag("--keep-layers", "retain the workspace for inspection or resume");
        var dryRun = Flag("--dry-run", "resolve and validate without applying plans");
        var singleLayer = Flag("--single-layer", "apply all plans in one working mount");
        var skipEvidence = Flag("--skip-evidence", "skip per-layer evidence capture");
        var resume = Text("--resume-workspace", "workspace to resume");
        var resumeLatest = Flag("--resume", "resume the newest workspace under the output directory");
        var oscdimg = Text("--oscdimg-path", "path to oscdimg.exe");
        var jsonEvents = Flag("--json-events", "write JSONL build events to stdout");

        command.Add(source);
        command.Add(index);
        command.Add(outputDirectory);
        command.Add(outputFormat);
        command.Add(createIso);
        command.Add(baseSize);
        command.Add(fast);
        command.Add(continueOnError);
        command.Add(keepLayers);
        command.Add(dryRun);
        command.Add(singleLayer);
        command.Add(skipEvidence);
        command.Add(resume);
        command.Add(resumeLatest);
        command.Add(oscdimg);
        command.Add(jsonEvents);
        command.SetAction(result => BuildHandler.ExecuteAsync(new(
            result.GetRequiredValue(source),
            result.GetRequiredValue(index),
            ReadSelection(result, selection),
            Path.GetFullPath(result.GetValue(outputDirectory)!),
            Cli.ParseOutputFormat(result.GetValue(outputFormat)!),
            result.GetValue(createIso),
            result.GetValue(fast),
            result.GetValue(continueOnError),
            result.GetValue(keepLayers),
            result.GetValue(dryRun),
            result.GetValue(singleLayer),
            result.GetValue(skipEvidence),
            result.GetValue(resume),
            result.GetValue(resumeLatest),
            result.GetValue(oscdimg),
            result.GetValue(baseSize),
            result.GetValue(jsonEvents))));
        return command;
    }

    private static Command Preview() {
        var command = new Command("preview", "report what each plan would change");
        var source = RequiredText("--source", "source ISO, WIM, ESD, or extracted media folder");
        var index = Required<int>("--index", "source image index");
        var selection = AddSelectionOptions(command);
        var outputDirectory = Text("--output-dir", "workspace output root directory");
        outputDirectory.DefaultValueFactory = _ => "out";
        var baseSize = Number("--base-vhdx-mb", "maximum dynamic base VHDX size in MB");
        baseSize.DefaultValueFactory = _ => BuildOptions.DefaultBaseVhdxMaximumMb;
        var json = Flag("--json", "write machine-readable JSON");
        command.Add(source);
        command.Add(index);
        command.Add(outputDirectory);
        command.Add(baseSize);
        command.Add(json);
        command.SetAction(result => PreviewHandler.ExecuteAsync(new(
            result.GetRequiredValue(source),
            result.GetRequiredValue(index),
            ReadSelection(result, selection),
            Path.GetFullPath(result.GetValue(outputDirectory)!),
            result.GetValue(baseSize),
            result.GetValue(json))));
        return command;
    }

    private static Command Layers() {
        var command = new Command("layer", "inspect, compare, extract, or capture layer states");

        var list = new Command("list", "list the layer chain in a workspace");
        var listWorkspace = Argument<string>("workspace", "build workspace directory");
        var listJson = Flag("--json", "write machine-readable JSON");
        list.Add(listWorkspace);
        list.Add(listJson);
        list.SetAction(result => LayerHandler.List(new(
            result.GetRequiredValue(listWorkspace),
            result.GetValue(listJson))));

        var diff = new Command("diff", "show file and registry changes between two layers");
        var diffWorkspace = Argument<string>("workspace", "build workspace directory");
        var from = Argument<int>("from", "starting layer index");
        var to = Argument<int>("to", "ending layer index");
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
        var extractWorkspace = Argument<string>("workspace", "build workspace directory");
        var extractLayer = Argument<int>("layer", "layer index");
        var imagePath = Argument<string>("image-path", "path relative to the image root");
        var destination = Argument<string>("destination", "destination file");
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
        var rollbackWorkspace = Argument<string>("workspace", "build workspace directory");
        var rollbackLayer = Argument<int>("layer", "layer index");
        var rollbackOutput = RequiredText("--output-file", "destination image file");
        var rollbackFormat = RequiredText("--format", "capture format");
        rollbackFormat.AcceptOnlyFromAmong("wim", "esd");
        var rollbackFast = Flag("--fast", "use faster, less conservative capture settings");
        rollback.Add(rollbackWorkspace);
        rollback.Add(rollbackLayer);
        rollback.Add(rollbackOutput);
        rollback.Add(rollbackFormat);
        rollback.Add(rollbackFast);
        rollback.SetAction(result => LayerHandler.Rollback(new(
            result.GetRequiredValue(rollbackWorkspace),
            result.GetRequiredValue(rollbackLayer),
            result.GetRequiredValue(rollbackOutput),
            Cli.ParseCaptureFormat(result.GetValue(rollbackFormat)!),
            result.GetValue(rollbackFast))));

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

    private static SelectionRequest ReadSelection(ParseResult result, SelectionSymbols symbols) => new(
        result.GetValue(symbols.PlansDirectory),
        result.GetValue(symbols.Profiles) ?? [],
        result.GetValue(symbols.Plans) ?? [],
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
        Option<string[]> Profiles,
        Option<string[]> Plans,
        Option<string[]> Sets);
}
