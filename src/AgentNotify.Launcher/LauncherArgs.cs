namespace AgentNotify.Launcher;

/// <summary>
/// Разобранный вызов лаунчера. Контракт намеренно узкий: лаунчер умеет только один сценарий
/// Планировщика — <c>run-due</c> с необязательным <c>--home</c>. Он не является универсальным
/// исполнителем команд: произвольные аргументы и пути отклоняются.
/// </summary>
public sealed record LauncherInvocation(string? Home)
{
    /// <summary>Аргументы дочернего AgentNotify.exe.</summary>
    public IReadOnlyList<string> ChildArguments()
    {
        if (Home is null) return new[] { LauncherArgs.SupportedCommand };
        return new[] { LauncherArgs.SupportedCommand, "--home", Home };
    }
}

public static class LauncherArgs
{
    public const string SupportedCommand = "run-due";
    public const string HomeOption = "--home";

    /// <summary>
    /// Разбирает argv лаунчера. Возвращает false и текст ошибки для любого вызова,
    /// выходящего за пределы узкого контракта (другая команда, лишние или неизвестные аргументы).
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, out LauncherInvocation? invocation, out string? error)
    {
        invocation = null;
        error = null;

        if (args.Count == 0)
        {
            error = "лаунчер поддерживает только команду run-due";
            return false;
        }

        if (!string.Equals(args[0], SupportedCommand, StringComparison.OrdinalIgnoreCase))
        {
            error = $"лаунчер поддерживает только команду {SupportedCommand}, получено: {args[0]}";
            return false;
        }

        string? home = null;
        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            if (!string.Equals(token, HomeOption, StringComparison.Ordinal))
            {
                error = $"неподдерживаемый аргумент лаунчера: {token}";
                return false;
            }
            if (home is not null)
            {
                error = "параметр --home указан более одного раза";
                return false;
            }
            if (i + 1 >= args.Count)
            {
                error = "параметр --home требует значение";
                return false;
            }
            var value = args[++i];
            if (string.IsNullOrWhiteSpace(value))
            {
                error = "параметр --home требует непустое значение";
                return false;
            }
            home = value;
        }

        invocation = new LauncherInvocation(home);
        return true;
    }
}
