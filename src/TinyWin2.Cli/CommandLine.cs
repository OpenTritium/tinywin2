using System.CommandLine;
using System.CommandLine.Help;
using TinyWin2.Cli.Commands;
using TinyWin2.Core.Pipeline;

namespace TinyWin2.Cli;

internal static class CommandLine {
    public static RootCommand CreateRootCommand() {
        var root = new RootCommand("layered Windows image slimming with explicit inputs, outputs, and workspaces");
        var help = new HelpAction();
        root.SetAction(parseResult => help.Invoke(parseResult));
        root.Add(Doctor());
        root.Add(Inspect());
        root.Add(Source());
        root.Add(Plans());
        root.Add(Profiles());
        root.Add(Build());
        root.Add(Preview());
        root.Add(Package());
        root.Add(Unattend());
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

        var validate = new Command("validate", "validate one plan JSON file (schema, dependencies, binding)");
        var validateFile = RequiredText("--file", "plan JSON file to validate");
        var validatePlansDirectory = Text("--plans-dir", "directory containing the full plan catalog");
        var validateJson = Flag("--json", "write machine-readable JSON");
        validate.Add(validateFile);
        validate.Add(validatePlansDirectory);
        validate.Add(validateJson);
        validate.SetAction(result => PlanHandler.Validate(new(
            result.GetRequiredValue(validateFile),
            result.GetValue(validatePlansDirectory),
            result.GetValue(validateJson))));

        command.Add(list);
        command.Add(show);
        command.Add(validate);
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

    private static Command Source() {
        var command = new Command("source", "inspect or validate an installation source: image, media folder, or ISO");

        var validate = new Command("validate", "check boot files, setup contract, and image indexes of a source");
        var input = RequiredText("--input", "image, media folder, or ISO to validate");
        var kind = Text("--kind", "assert the input kind: image, media, or iso; inferred from the input when omitted");
        kind.AcceptOnlyFromAmong("image", "media", "iso");
        var json = Flag("--json", "write machine-readable JSON");
        validate.Add(input);
        validate.Add(kind);
        validate.Add(json);
        validate.SetAction(result => SourceValidateHandler.ExecuteAsync(new(
            result.GetRequiredValue(input),
            result.GetValue(kind),
            result.GetValue(json))));

        command.Add(validate);
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
        var singleLayer = Flag("--single-layer",
            "fast mode: apply all plans in one mount; progress is checkpointed for --resume");
        var skipEvidence = Flag("--skip-evidence", "skip per-layer evidence capture");
        var resume = Flag("--resume", "resume from the last completed plan checkpoint in the workspace");
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
            Cli.ResolveExportOptions(
                result.GetValue(export.Fast),
                result.GetValue(export.Compression),
                result.GetValue(export.Verify),
                result.GetValue(export.NoVerify),
                result.GetValue(export.CheckIntegrity)),
            new BuildSwitches(
                result.GetValue(continueOnError),
                result.GetValue(dryRun),
                result.GetValue(singleLayer),
                result.GetValue(skipEvidence),
                result.GetValue(export.Fast),
                result.GetValue(resume),
                result.GetValue(overwrite),
                result.GetValue(baseSize),
                result.GetValue(jsonEvents)))));
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
        var installImage = RequiredText("--install-image", "built install.wim or install.esd");
        var output = RequiredText("--output", "destination .iso file");
        var workspace = RequiredText("--workspace", "temporary media staging directory");
        var oscdimg = RequiredText("--oscdimg", "path to oscdimg.exe");
        var unattended = Text("--unattend", "Autounattend.xml to place at the media root: a file path, 'auto' to generate one, or 'none'");
        unattended.DefaultValueFactory = _ => "auto";
        var firmware = Text("--firmware", "firmware layout for the generated unattend: bios (MBR) or uefi (GPT)");
        firmware.DefaultValueFactory = _ => "bios";
        var layout = Text("--layout",
            "partition layout: standard, or minimal (stages a SYSTEM SetupComplete.cmd that moves WinRE to C: and deletes the recovery partition)");
        layout.DefaultValueFactory = _ => "standard";
        var kioskUser = Text("--kiosk-user", "local administrator created by the generated unattend");
        kioskUser.DefaultValueFactory = _ => "TinyWin";
        var kioskPassword = Text("--kiosk-password", "password for the generated unattend account (plain text)");
        kioskPassword.DefaultValueFactory = _ => "TinyWin2026Temp";
        var productKey = Text("--product-key",
            "25-character product key for the generated unattend; required on Windows 11 24H2+/26H2 media, where an empty key no longer skips the setup key page");
        var overwrite = Flag("--overwrite", "replace an existing ISO");
        iso.Add(input);
        iso.Add(installImage);
        iso.Add(output);
        iso.Add(workspace);
        iso.Add(oscdimg);
        iso.Add(unattended);
        iso.Add(firmware);
        iso.Add(layout);
        iso.Add(kioskUser);
        iso.Add(kioskPassword);
        iso.Add(productKey);
        iso.Add(overwrite);
        iso.SetAction(result => PackageHandler.CreateIsoAsync(new(
            result.GetRequiredValue(input),
            result.GetRequiredValue(installImage),
            result.GetRequiredValue(output),
            result.GetRequiredValue(workspace),
            result.GetRequiredValue(oscdimg),
            result.GetValue(overwrite),
            result.GetValue(unattended),
            result.GetValue(firmware)!,
            result.GetValue(layout)!,
            result.GetValue(kioskUser)!,
            result.GetValue(kioskPassword)!,
            result.GetValue(productKey))));
        command.Add(iso);
        return command;
    }

    private static Command Unattend() {
        var command = new Command("unattend", "generate and validate an Autounattend.xml for kiosk VMs");

        var generate = new Command("generate",
            "write an Autounattend.xml that wipes the disk, skips OOBE and signs in a local administrator");
        var output = RequiredText("--output", "destination Autounattend.xml path");
        var firmware = Text("--firmware", "target firmware layout: bios (MBR) or uefi (GPT)");
        firmware.DefaultValueFactory = _ => "bios";
        var user = Text("--user", "local administrator account name");
        user.DefaultValueFactory = _ => "TinyWin";
        var password = Text("--password", "password stored in plain text in the answer file");
        password.DefaultValueFactory = _ => "TinyWin2026Temp";
        var computerName = Text("--computer-name", "target computer name (max 15 characters)");
        var imageIndex = Number("--image-index", "image index to install from the media");
        imageIndex.DefaultValueFactory = _ => 1;
        var productKey = Text("--product-key", "optional 25-character product key");
        var locale = Text("--locale", "language, input, system and user locale");
        var timeZone = Text("--timezone", "target time zone");
        generate.Add(output);
        generate.Add(firmware);
        generate.Add(user);
        generate.Add(password);
        generate.Add(computerName);
        generate.Add(imageIndex);
        generate.Add(productKey);
        generate.Add(locale);
        generate.Add(timeZone);
        generate.SetAction(result => UnattendHandler.Generate(new(
            result.GetRequiredValue(output),
            result.GetValue(firmware)!,
            result.GetValue(user)!,
            result.GetValue(password)!,
            result.GetValue(computerName),
            result.GetValue(imageIndex),
            result.GetValue(productKey),
            result.GetValue(locale),
            result.GetValue(timeZone))));

        var validate = new Command("validate", "check an Autounattend.xml for structural and consistency errors");
        var file = RequiredText("--file", "Autounattend.xml to validate");
        var json = Flag("--json", "write machine-readable JSON");
        validate.Add(file);
        validate.Add(json);
        validate.SetAction(result => UnattendHandler.Validate(new(
            result.GetRequiredValue(file),
            result.GetValue(json))));

        command.Add(generate);
        command.Add(validate);
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

        var capture = new Command("capture", "capture a layer state as a WIM or ESD without modifying the workspace");
        var captureWorkspace = RequiredText("--workspace", "build workspace directory");
        var captureLayer = Required<int>("--layer", "layer index");
        var captureOutput = RequiredText("--output", "destination .wim or .esd file");
        var captureFormat = RequiredText("--format", "capture format");
        captureFormat.AcceptOnlyFromAmong("wim", "esd");
        var captureExport = AddExportOptions(capture);
        capture.Add(captureWorkspace);
        capture.Add(captureLayer);
        capture.Add(captureOutput);
        capture.Add(captureFormat);
        capture.SetAction(result => LayerHandler.Capture(new(
            result.GetRequiredValue(captureWorkspace),
            result.GetRequiredValue(captureLayer),
            result.GetRequiredValue(captureOutput),
            Cli.ParseCaptureFormat(result.GetRequiredValue(captureFormat)),
            Cli.ResolveExportOptions(
                result.GetValue(captureExport.Fast),
                result.GetValue(captureExport.Compression),
                result.GetValue(captureExport.Verify),
                result.GetValue(captureExport.NoVerify),
                result.GetValue(captureExport.CheckIntegrity)))));

        command.Add(list);
        command.Add(diff);
        command.Add(extract);
        command.Add(capture);
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
