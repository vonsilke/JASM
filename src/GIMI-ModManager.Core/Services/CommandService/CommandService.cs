﻿using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using GIMI_ModManager.Core.Helpers;
using Serilog;
using static GIMI_ModManager.Core.Services.CommandService.RunningCommandChangedEventArgs;

namespace GIMI_ModManager.Core.Services.CommandService;

public sealed class CommandService(ILogger logger)
{
    private readonly ILogger _logger = logger.ForContext<CommandService>();

    private List<CommandDefinition> _commands = new();

    private readonly ConcurrentDictionary<Guid, ProcessCommand> _runningCommands = [];

    private string _jsonCommandFilePath = "";

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public bool IsInitialized { get; private set; }

    public event EventHandler<RunningCommandChangedEventArgs> RunningCommandsChanged;

    public async Task InitializeAsync(string appDataDirectoryPath)
    {
        if (IsInitialized)
            return;

        if (!Directory.Exists(appDataDirectoryPath))
            Directory.CreateDirectory(appDataDirectoryPath);

        _jsonCommandFilePath = Path.Combine(appDataDirectoryPath, "commands.json");

        if (File.Exists(_jsonCommandFilePath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_jsonCommandFilePath).ConfigureAwait(false);
                var jsonCommands = JsonSerializer.Deserialize<CommandRootJson>(json) ?? new CommandRootJson();

                _commands = jsonCommands.Commands.Select(j => CommandDefinition.FromJson(j)).ToList();
                if (jsonCommands.StartGameCommand is not null)
                    _commands.Add(CommandDefinition.FromJson(jsonCommands.StartGameCommand, isGameStartCommand: true));

                if (jsonCommands.StartGameModelImporter is not null)
                    _commands.Add(CommandDefinition.FromJson(jsonCommands.StartGameModelImporter,
                        isModelImporterCommand: true));
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to read command root json. Creating new one");
                File.Move(_jsonCommandFilePath, _jsonCommandFilePath + ".invalid", overwrite: true);
                await File.WriteAllTextAsync(_jsonCommandFilePath,
                        JsonSerializer.Serialize(new CommandRootJson(), _jsonOptions))
                    .ConfigureAwait(false);
            }
        }
        else
        {
            _logger.Information("No command root json found, creating new one");
            await File.WriteAllTextAsync(_jsonCommandFilePath,
                    JsonSerializer.Serialize(new CommandRootJson(), _jsonOptions))
                .ConfigureAwait(false);
        }


        Debug.Assert(File.Exists(_jsonCommandFilePath));

        IsInitialized = true;
    }


    private ProcessCommand InternalCreateCommand(InternalCreateCommandOptions options)
    {
        var commandContext = new CommandContext
        {
            SpecialVariables = options.SpecialVariablesInput
        };

        options.ExecutionOptions.IsReadOnly = true;

        var startInfo = new ProcessStartInfo
        {
            FileName = SpecialVariables.ReplaceVariables(options.ExecutionOptions.Command,
                options.SpecialVariablesInput),
            Arguments = SpecialVariables.ReplaceVariables(options.ExecutionOptions.Arguments,
                options.SpecialVariablesInput),
            WorkingDirectory = SpecialVariables.ReplaceVariables(options.ExecutionOptions.WorkingDirectory,
                options.SpecialVariablesInput),
            UseShellExecute = options.ExecutionOptions.UseShellExecute,
            CreateNoWindow = !options.ExecutionOptions.CreateWindow,
            Verb = options.ExecutionOptions.RunAsAdmin ? "runas" : null
        };

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };


        if (options.ExecutionOptions.CanRedirectInputOutput())
        {
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardInput = true;
        }

        var command = new ProcessCommand(process, options, commandContext);

        command.Started += (_, _) =>
        {
            _runningCommands.TryAdd(commandContext.Id, command);
            RunningCommandsChanged?.Invoke(this,
                new RunningCommandChangedEventArgs(CommandChangeType.Added, command));
        };
        command.Exited += (_, _) =>
        {
            var removed = _runningCommands.TryRemove(commandContext.Id, out _);
            if (removed)
                RunningCommandsChanged?.Invoke(this,
                    new RunningCommandChangedEventArgs(CommandChangeType.Removed, command));
        };

        return command;
    }

    public ProcessCommand CreateCommand(CommandDefinition definition, SpecialVariablesInput? specialVariables = null)
    {
        if (specialVariables is not null)
            specialVariables.IsReadOnly = true;

        return InternalCreateCommand(new InternalCreateCommandOptions
        {
            DisplayName = definition.CommandDisplayName,
            CommandDefinitionId = definition.Id,
            KillOnMainAppExit = definition.KillOnMainAppExit,
            SpecialVariablesInput = specialVariables,
            ExecutionOptions = definition.ExecutionOptions.Clone()
        });
    }

    public Task SaveCommandDefinitionAsync(CommandDefinition commandDefinition)
    {
        commandDefinition.ExecutionOptions.IsReadOnly = true;
        _commands.Add(commandDefinition);

        return SaveCommandsAsync();
    }

    public Task SetSpecialCommands(Guid commandDefinitionId, bool gameStart, bool modelImporterStart)
    {
        if (gameStart)
        {
            var existingGameStartCommand = _commands.FirstOrDefault(c => c.IsGameStartCommand);
            if (existingGameStartCommand is not null)
                existingGameStartCommand.IsGameStartCommand = false;

            var command = _commands.FirstOrDefault(c => c.Id == commandDefinitionId);
            if (command is null)
                throw new InvalidOperationException("Command does not exist");
            command.IsGameStartCommand = true;
        }

        if (modelImporterStart)
        {
            var existingModelImporterCommand = _commands.FirstOrDefault(c => c.IsModelImporterCommand);
            if (existingModelImporterCommand is not null)
                existingModelImporterCommand.IsModelImporterCommand = false;

            var command = _commands.FirstOrDefault(c => c.Id == commandDefinitionId);
            if (command is null)
                throw new InvalidOperationException("Command does not exist");
            command.IsModelImporterCommand = true;
        }

        return SaveCommandsAsync();
    }


    public Task DeleteCommandDefinitionAsync(Guid commandId)
    {
        var command = _commands.FirstOrDefault(c => c.Id == commandId);

        if (command is null)
            return Task.CompletedTask;

        _commands.Remove(command);

        return SaveCommandsAsync();
    }

    public async Task<CommandDefinition> UpdateCommandDefinitionAsync(Guid existingCommandId,
        CommandDefinition newCommandDefinition)
    {
        var existingCommand = _commands.FirstOrDefault(c => c.Id == existingCommandId);

        if (existingCommand is null)
            throw new InvalidOperationException("Command does not exist");

        newCommandDefinition.ExecutionOptions.IsReadOnly = true;
        newCommandDefinition.UpdateId(newCommandDefinition.Id);

        newCommandDefinition.IsGameStartCommand = existingCommand.IsGameStartCommand;
        newCommandDefinition.IsModelImporterCommand = existingCommand.IsModelImporterCommand;

        _commands.Remove(existingCommand);
        _commands.Add(newCommandDefinition);

        try
        {
            await SaveCommandsAsync().ConfigureAwait(false);
            return newCommandDefinition;
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to update command, reverting...");
            _commands.Remove(newCommandDefinition);
            _commands.Add(existingCommand);
        }

        try
        {
            await SaveCommandsAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _logger.Error(e, "Failed to revert command update");
            throw;
        }

        throw new InvalidOperationException("Failed to update command ");
    }

    public Task<IReadOnlyList<CommandDefinition>> GetCommandDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IReadOnlyList<CommandDefinition>>(_commands.ToList());
    }

    public Task<CommandDefinition?> GetCommandDefinitionAsync(Guid commandId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_commands.FirstOrDefault(c => c.Id == commandId));
    }

    public Task<IReadOnlyList<ProcessCommand>> GetRunningCommandsAsync()
    {
        return Task.FromResult<IReadOnlyList<ProcessCommand>>(_runningCommands.Values.ToList());
    }


    private async Task SaveCommandsAsync()
    {
        var jsonRoot = new CommandRootJson
        {
            Commands = _commands
                .Where(c => c is { IsGameStartCommand: false, IsModelImporterCommand: false })
                .Select(ToJsonCommandDefinition)
                .ToArray()
        };

        var gameStartCommand = _commands.FirstOrDefault(c => c.IsGameStartCommand);
        if (gameStartCommand is not null)
            jsonRoot.StartGameCommand = ToJsonCommandDefinition(gameStartCommand);

        var modelImporterCommand = _commands.FirstOrDefault(c => c.IsModelImporterCommand);
        if (modelImporterCommand is not null)
            jsonRoot.StartGameModelImporter = ToJsonCommandDefinition(modelImporterCommand);


        await File.WriteAllTextAsync(_jsonCommandFilePath, JsonSerializer.Serialize(jsonRoot, _jsonOptions))
                .ConfigureAwait(false);
    }

    private JsonCommandDefinition ToJsonCommandDefinition(CommandDefinition commandDefinition)
    {
        return new JsonCommandDefinition
        {
            Id = commandDefinition.Id,
            CreateTime = DateTime.Now,
            DisplayName = commandDefinition.CommandDisplayName,
            Command = commandDefinition.ExecutionOptions.Command,
            Arguments = commandDefinition.ExecutionOptions.Arguments,
            WorkingDirectory = commandDefinition.ExecutionOptions.WorkingDirectory,
            UseShellExecute = commandDefinition.ExecutionOptions.UseShellExecute,
            CreateWindow = commandDefinition.ExecutionOptions.CreateWindow,
            RunAsAdmin = commandDefinition.ExecutionOptions.RunAsAdmin,
            KillOnMainAppExit = commandDefinition.KillOnMainAppExit
        };
    }

    public void Cleanup()
    {
        var runningCommands = _runningCommands.Values.ToArray();

        foreach (var command in runningCommands)
        {
            if (command is { IsRunning: true, InternalCreateOptions.KillOnMainAppExit: true })
                _ = command.KillAsync();
        }
    }
}

public class RunningCommandChangedEventArgs(
    CommandChangeType commandChangeType,
    ProcessCommand command)
    : EventArgs
{
    public CommandChangeType ChangeType { get; } = commandChangeType;

    public ProcessCommand Command { get; } = command;

    public enum CommandChangeType
    {
        Added,
        Removed
    }
}

public class CommandDefinition
{
    public Guid Id { get; private set; } = Guid.NewGuid();

    public required string CommandDisplayName { get; init; }

    // If true, the process will be killed when the application closes
    public required bool KillOnMainAppExit { get; init; }
    public required CommandExecutionOptions ExecutionOptions { get; init; }

    public bool IsGameStartCommand { get; internal set; }

    public bool IsModelImporterCommand { get; internal set; }

    /// <summary>
    /// This will return the full command with all variables replaced
    /// </summary>
    public (string FullCommand, string? WorkingDirectory) GetFullCommand(
        SpecialVariablesInput? specialVariablesInput)
    {
        var fullCommand = SpecialVariables.ReplaceVariables(
            ExecutionOptions.Command + " " + ExecutionOptions.Arguments,
            specialVariablesInput);

        var workingDirectory = SpecialVariables.ReplaceVariables(ExecutionOptions.WorkingDirectory,
            specialVariablesInput);

        return (fullCommand, workingDirectory);
    }

    internal void UpdateId(Guid newId) => Id = newId;


    internal static CommandDefinition FromJson(JsonCommandDefinition jsonCommandDefinition,
        bool isGameStartCommand = false, bool isModelImporterCommand = false)
    {
        return new CommandDefinition()
        {
            Id = jsonCommandDefinition.Id,
            CommandDisplayName = jsonCommandDefinition.DisplayName,
            KillOnMainAppExit = jsonCommandDefinition.KillOnMainAppExit,
            IsGameStartCommand = isGameStartCommand,
            IsModelImporterCommand = isModelImporterCommand,
            ExecutionOptions = new CommandExecutionOptions()
            {
                Command = jsonCommandDefinition.Command,
                WorkingDirectory = jsonCommandDefinition.WorkingDirectory,
                UseShellExecute = jsonCommandDefinition.UseShellExecute,
                Arguments = jsonCommandDefinition.Arguments,
                CreateWindow = jsonCommandDefinition.CreateWindow,
                RunAsAdmin = jsonCommandDefinition.RunAsAdmin
            }
        };
    }
}

internal class InternalCreateCommandOptions
{
    public required Guid CommandDefinitionId { get; init; }

    public required SpecialVariablesInput? SpecialVariablesInput { get; init; }

    private readonly string? _displayName;

    public string DisplayName
    {
        get =>
            _displayName ??
            SpecialVariables.ReplaceVariables(ExecutionOptions.Command + " " + ExecutionOptions.Arguments,
                SpecialVariablesInput ?? new SpecialVariablesInput());

        init => _displayName = value;
    }


    public bool KillOnMainAppExit { get; init; }

    public required CommandExecutionOptions ExecutionOptions { get; set; }
}

/// <summary>
/// This is returned when a command is created, is used to track and interact with the process
/// </summary>
public sealed class ProcessCommand
{
    private readonly Process _process;
    private readonly CommandContext _context;

    internal InternalCreateCommandOptions InternalCreateOptions { get; }
    public CommandExecutionOptions Options => InternalCreateOptions.ExecutionOptions;

    public Guid RunId => _context.Id;

    public Guid CommandDefinitionId => InternalCreateOptions.CommandDefinitionId;

    public string DisplayName => InternalCreateOptions.DisplayName;

    public string FullCommand => _process.StartInfo.FileName + " " + _process.StartInfo.Arguments;

    internal ProcessCommand(Process process, InternalCreateCommandOptions options,
        CommandContext context)
    {
        _process = process;
        InternalCreateOptions = options;
        _context = context;

        CanWriteInputReadOutput = InternalCreateOptions.ExecutionOptions.CanRedirectInputOutput();
    }

    public bool HasBeenStarted { get; private set; }
    public bool CanWriteInputReadOutput { get; private set; }

    public bool HasExited { get; private set; }

    public bool IsRunning => !HasExited;

    public int ExitCode { get; private set; } = 1337;

    public event EventHandler? Started;
    public event EventHandler? Exited;
    public event EventHandler<DataReceivedEventArgs>? OutputDataReceived;
    public event EventHandler<DataReceivedEventArgs>? ErrorDataReceived;


    public bool Start()
    {
        if (HasBeenStarted)
            throw new InvalidOperationException("Cannot start a command that has already been started");

        _process.Exited += ExitHandler;


        var currentWorkingDirectory = Environment.CurrentDirectory;

        if ((Options.UseShellExecute || Options.RunAsAdmin) && !_process.StartInfo.WorkingDirectory.IsNullOrEmpty())
        {
            if (Directory.Exists(_process.StartInfo.WorkingDirectory))
            {
                Environment.CurrentDirectory = _process.StartInfo.WorkingDirectory;
            }
            else
            {
                throw new InvalidOperationException(
                    "Working directory does not exist for a command using shell execute");
            }
        }

        bool result;
        try
        {
            result = _process.Start();
        }
        catch (Exception e)
        {
            _process.Exited -= ExitHandler;
            throw;
        }
        finally
        {
            Environment.CurrentDirectory = currentWorkingDirectory;
        }

        HasBeenStarted = true;
        _context.StartTime = DateTime.Now;

        if (CanWriteInputReadOutput)
        {
            _process.OutputDataReceived += (_, args) => OutputDataReceived?.Invoke(this, args);
            _process.ErrorDataReceived += (_, args) => ErrorDataReceived?.Invoke(this, args);

            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        Started?.Invoke(this, EventArgs.Empty);

        _process.Refresh();
        return result;
    }

    private void ExitHandler(object? sender, EventArgs args)
    {
        HasExited = true;
        ExitCode = _process.ExitCode;
        _context.EndTime = DateTime.Now;
        _process.Dispose();
        Exited?.Invoke(this, args);
    }

    public async Task WaitForExitAsync()
    {
        if (HasExited)
            return;

        // TODO: Use task completion source to wait for the process to exit
        await Task.Run(() => _process.WaitForExit()).ConfigureAwait(false);
    }


    public async Task WriteInputAsync(string? input, bool appendNewLine = true)
    {
        if (!CanWriteInputReadOutput)
            throw new InvalidOperationException("Cannot write input to a process that uses shell execute");

        if (HasExited)
            throw new InvalidOperationException("Cannot write input to a process that has exited");

        var inputToWrite = input + (appendNewLine ? Environment.NewLine : string.Empty);

        await _process.StandardInput.WriteAsync(inputToWrite).ConfigureAwait(false);
    }


    public async Task<int> KillAsync()
    {
        if (HasExited)
            return ExitCode;

        _process.Kill();
        await WaitForExitAsync().ConfigureAwait(false);
        return ExitCode;
    }
}

internal class CommandRootJson
{
    public JsonCommandDefinition[] Commands { get; set; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonCommandDefinition? StartGameCommand { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonCommandDefinition? StartGameModelImporter { get; set; }
}

internal class JsonCommandDefinition
{
    public required Guid Id { get; set; }
    public required DateTime CreateTime { get; set; }
    public required string DisplayName { get; set; }
    public required string Command { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Arguments { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkingDirectory { get; set; }

    public required bool UseShellExecute { get; set; }
    public required bool CreateWindow { get; set; }
    public required bool RunAsAdmin { get; set; }

    public required bool KillOnMainAppExit { get; set; }
}