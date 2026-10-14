using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Security.Principal;
using System.Text.Json;

namespace DnsCli;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                if (!OperatingSystem.IsWindows())
                {
                    Console.Error.WriteLine("Ошибка: dnsctl работает только в Windows.");
                    return 1;
                }

                if (!IsAdministrator())
                    return RelaunchAsAdministrator(args);

                return InteractiveMenu("Config");
            }

            var options = Options.Parse(args);
            if (options.Help)
            {
                PrintHelp();
                return 0;
            }

            if (options.Error is not null)
            {
                Console.Error.WriteLine($"Ошибка: {options.Error}");
                PrintHelp();
                return 2;
            }

            if (!OperatingSystem.IsWindows())
            {
                Console.Error.WriteLine("Ошибка: dnsctl работает только в Windows.");
                return 1;
            }

            if (options.Command is "apply" or "reset" && !IsAdministrator())
            {
                return RelaunchAsAdministrator(args);
            }

            return options.Command switch
            {
                "profiles" => ListProfiles(options.ConfigDirectory),
                "list" => ListAdapters(),
                "status" => Status(options),
                "apply" => Apply(options),
                "reset" => Reset(options),
                _ => Fail("Не указана команда.")
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }
    }

    private static int InteractiveMenu(string configDirectory)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (!Console.IsOutputRedirected)
            Console.CursorVisible = false;

        Adapter? adapter = null;
        try
        {
            adapter = Windows.ResolveAdapter(null);
        }
        catch
        {
        }

        string? selectedProfile = null;
        var selectedIndex = 0;

        var menuItems = new (string Key, string Title, string Hint)[]
        {
            ("1", "Выбрать конфиг", "загрузить DNS-профиль из Config"),
            ("2", "Очистить DNS", "вернуть автоматические настройки DHCP"),
            ("3", "Выбрать адаптер", "сетевой интерфейс Windows"),
            ("4", "Применить выбранный конфиг", "установить IPv4 и IPv6 DNS"),
            ("5", "Статус DNS / DoH", "проверить активные DNS и шифрование"),
            ("0", "Выход", "закрыть DNSCTL")
        };

        while (true)
        {
            if (!Console.IsOutputRedirected)
                Console.Clear();

            Ui.Header();
            Ui.Line("Адаптер", adapter?.Name ?? "не выбран");
            Ui.Line("Конфиг", selectedProfile is null ? "не выбран" : Path.GetFileNameWithoutExtension(selectedProfile));
            Ui.Separator();

            for (var i = 0; i < menuItems.Length; i++)
            {
                Ui.MenuItem(i == selectedIndex, menuItems[i].Key, menuItems[i].Title, menuItems[i].Hint);
            }

            Ui.Separator();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Навигация: ↑ / W и ↓ / S, выбор: Enter (или цифры 0-5)");
            Console.ResetColor();

            var key = Console.ReadKey(intercept: true);
            if (Ui.IsUp(key))
            {
                selectedIndex = (selectedIndex - 1 + menuItems.Length) % menuItems.Length;
                continue;
            }

            if (Ui.IsDown(key))
            {
                selectedIndex = (selectedIndex + 1) % menuItems.Length;
                continue;
            }

            string choice;
            if (key.Key == ConsoleKey.Enter)
            {
                choice = menuItems[selectedIndex].Key;
            }
            else if (char.IsDigit(key.KeyChar))
            {
                choice = key.KeyChar.ToString();
                var idx = Array.FindIndex(menuItems, m => m.Key == choice);
                if (idx >= 0)
                    selectedIndex = idx;
            }
            else if (key.Key == ConsoleKey.Escape)
            {
                return 0;
            }
            else
            {
                continue;
            }

            try
            {
                switch (choice)
                {
                    case "1":
                        selectedProfile = ChooseProfile(configDirectory);
                        break;

                    case "2":
                        adapter ??= Windows.ResolveAdapter(null);
                        ResetAdapter(adapter);
                        Ui.Success("DNS очищен: используется DHCP.");
                        Ui.Pause();
                        break;

                    case "3":
                        adapter = ChooseAdapter();
                        break;

                    case "4":
                        if (selectedProfile is null)
                        {
                            Ui.Warning("Сначала выберите конфиг.");
                            Ui.Pause();
                            break;
                        }

                        adapter ??= Windows.ResolveAdapter(null);
                        ApplyProfilePath(selectedProfile, adapter);
                        Ui.Success("DNS-профиль успешно применён.");

                        var p = LoadProfile(selectedProfile);
                        if (!string.IsNullOrWhiteSpace(p.Doh))
                        {
                            Console.ForegroundColor = ConsoleColor.Cyan;
                            Console.WriteLine("\n  [i] DoH активирован: DNS-клиент Windows шифрует запросы через HTTPS (порт 443).");
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.WriteLine("      Примечание: Windows 11 в графическом интерфейсе «Параметры» может писать");
                            Console.WriteLine("      «Без шифрования» — это косметический баг GUI при настройке через CLI.");
                            Console.ResetColor();
                        }
                        Ui.Pause();
                        break;

                    case "5":
                        adapter ??= Windows.ResolveAdapter(null);
                        Windows.ShowStatus(adapter.Name);
                        Ui.Pause();
                        break;

                    case "0":
                        return 0;

                    default:
                        Ui.Warning("Выберите пункт из меню.");
                        Ui.Pause();
                        break;
                }
            }
            catch (Exception ex)
            {
                Ui.Error(ex.Message);
                Ui.Pause();
            }
        }
    }

    private static string? ChooseProfile(string directory)
    {
        var files = ProfileSources(directory);
        if (files.Length == 0)
        {
            Ui.Warning($"В папке {Path.GetFullPath(directory)} нет JSON-конфигов и встроенные профили отсутствуют.");
            Ui.Pause();
            return null;
        }

        var profiles = files.Select(LoadProfile).ToArray();
        var selectedIndex = 0;

        while (true)
        {
            if (!Console.IsOutputRedirected)
                Console.Clear();

            Ui.Header();
            Console.WriteLine("  ДОСТУПНЫЕ DNS-КОНФИГИ\n");

            for (var i = 0; i < files.Length; i++)
            {
                var profile = profiles[i];
                var isSelected = i == selectedIndex;

                if (isSelected)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write("  ▶ ");
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine($" {profile.Name,-30} ");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write($"    {i + 1}. ");
                    Console.ResetColor();
                    Console.WriteLine(profile.Name);
                }

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"       IPv4: {string.Join(", ", profile.Ipv4)} | IPv6: {string.Join(", ", profile.Ipv6)}");
                if (!string.IsNullOrWhiteSpace(profile.Doh))
                {
                    Console.WriteLine($"       DoH:  {profile.Doh}");
                }
                Console.ResetColor();
            }

            Console.WriteLine();
            Ui.Separator();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Навигация: ↑ / W и ↓ / S, выбор: Enter, Esc — назад");
            Console.ResetColor();

            var key = Console.ReadKey(intercept: true);
            if (Ui.IsUp(key))
            {
                selectedIndex = (selectedIndex - 1 + files.Length) % files.Length;
                continue;
            }

            if (Ui.IsDown(key))
            {
                selectedIndex = (selectedIndex + 1) % files.Length;
                continue;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                Ui.Success($"Выбран конфиг: {ProfileName(files[selectedIndex])}");
                Ui.Pause();
                return files[selectedIndex];
            }

            if (key.Key == ConsoleKey.Escape)
            {
                return null;
            }

            if (char.IsDigit(key.KeyChar))
            {
                var digit = key.KeyChar - '0';
                if (digit >= 1 && digit <= files.Length)
                {
                    Ui.Success($"Выбран конфиг: {ProfileName(files[digit - 1])}");
                    Ui.Pause();
                    return files[digit - 1];
                }
            }
        }
    }

    private static Adapter ChooseAdapter()
    {
        var adapters = Windows.GetAdapters();
        if (adapters.Count == 0)
            throw new InvalidOperationException("Сетевые адаптеры не найдены.");

        var selectedIndex = 0;

        while (true)
        {
            if (!Console.IsOutputRedirected)
                Console.Clear();

            Ui.Header();
            Console.WriteLine("  ДОСТУПНЫЕ СЕТЕВЫЕ АДАПТЕРЫ\n");

            for (var i = 0; i < adapters.Count; i++)
            {
                var adapter = adapters[i];
                var isSelected = i == selectedIndex;
                var isUp = adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase);

                if (isSelected)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write("  ▶ ");
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine($" {adapter.Name} [{adapter.Status}] ");
                    Console.ResetColor();
                }
                else
                {
                    Console.ForegroundColor = isUp ? ConsoleColor.Green : ConsoleColor.DarkGray;
                    Console.WriteLine($"    {i + 1}. {adapter.Name} [{adapter.Status}]");
                    Console.ResetColor();
                }

                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"       {adapter.Description}");
                Console.ResetColor();
            }

            Console.WriteLine();
            Ui.Separator();
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  Навигация: ↑ / W и ↓ / S, выбор: Enter, Esc — назад");
            Console.ResetColor();

            var key = Console.ReadKey(intercept: true);
            if (Ui.IsUp(key))
            {
                selectedIndex = (selectedIndex - 1 + adapters.Count) % adapters.Count;
                continue;
            }

            if (Ui.IsDown(key))
            {
                selectedIndex = (selectedIndex + 1) % adapters.Count;
                continue;
            }

            if (key.Key == ConsoleKey.Enter)
            {
                return adapters[selectedIndex];
            }

            if (key.Key == ConsoleKey.Escape)
            {
                throw new OperationCanceledException();
            }

            if (char.IsDigit(key.KeyChar))
            {
                var digit = key.KeyChar - '0';
                if (digit >= 1 && digit <= adapters.Count)
                {
                    return adapters[digit - 1];
                }
            }
        }
    }

    private static void ApplyProfilePath(string path, Adapter adapter)
    {
        RequireAdministrator();
        var profile = LoadProfile(path);
        if (profile.Ipv4.Count == 0 && profile.Ipv6.Count == 0)
            throw new InvalidDataException("Профиль не содержит DNS-серверов.");

        Windows.SetDns(adapter.Name, profile.Ipv4, profile.Ipv6, profile.Doh);
    }

    private static void ResetAdapter(Adapter adapter)
    {
        RequireAdministrator();
        Windows.ResetDns(adapter.Name);
    }

    private static int ListProfiles(string directory)
    {
        var files = ProfileSources(directory);
        if (files.Length == 0)
        {
            Console.WriteLine($"Профили не найдены: {Path.GetFullPath(directory)}");
            return 0;
        }

        foreach (var file in files)
        {
            var profile = LoadProfile(file);
            Console.WriteLine($"{ProfileName(file),-24} {profile.Name}");
        }

        return 0;
    }

    private static int ListAdapters()
    {
        foreach (var adapter in Windows.GetAdapters())
        {
            Console.WriteLine($"{adapter.Name} | {adapter.Status} | {adapter.Description}");
        }

        return 0;
    }

    private static int Apply(Options options)
    {
        RequireAdministrator();
        var path = ResolveProfile(options.Profile!, options.ConfigDirectory);
        var profile = LoadProfile(path);
        var adapter = Windows.ResolveAdapter(options.Adapter);

        if (profile.Ipv4.Count == 0 && profile.Ipv6.Count == 0)
            return Fail("Профиль не содержит DNS-серверов.");

        Console.WriteLine($"Профиль: {profile.Name}");
        Console.WriteLine($"Адаптер: {adapter.Name}");
        Windows.SetDns(adapter.Name, profile.Ipv4, profile.Ipv6, profile.Doh);
        Console.WriteLine("DNS-настройки применены.");

        if (!string.IsNullOrWhiteSpace(profile.Doh))
        {
            Console.WriteLine($"DoH активирован: шаблон {profile.Doh}");
            Console.WriteLine("Примечание: В «Параметрах» Windows 11 может отображаться «Без шифрования» — это косметический баг GUI.");
        }

        return 0;
    }

    private static int Status(Options options)
    {
        Windows.ShowStatus(options.Adapter);
        return 0;
    }

    private static int Reset(Options options)
    {
        RequireAdministrator();
        var adapter = Windows.ResolveAdapter(options.Adapter);
        Windows.ResetDns(adapter.Name);
        Console.WriteLine($"DNS на адаптере «{adapter.Name}» снова получается автоматически (DHCP).");
        return 0;
    }

    private static DnsProfile LoadProfile(string path)
    {
        using var stream = path.StartsWith("embedded:", StringComparison.Ordinal)
            ? OpenEmbeddedProfile(path["embedded:".Length..])
            : File.OpenRead(path);

        var profile = JsonSerializer.Deserialize<DnsProfile>(stream, JsonOptions)
            ?? throw new InvalidDataException("Пустой JSON-профиль.");

        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? ProfileName(path) : profile.Name;
        profile.Ipv4 = Validate(profile.Ipv4, "IPv4");
        profile.Ipv6 = Validate(profile.Ipv6, "IPv6");
        return profile;
    }

    private static string[] ProfileSources(string directory)
    {
        var files = Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json").OrderBy(x => x).ToArray()
            : [];

        if (files.Length > 0)
            return files;

        return typeof(Program).Assembly.GetManifestResourceNames()
            .Where(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x)
            .Select(x => "embedded:" + x)
            .ToArray();
    }

    private static string ProfileName(string source)
    {
        var value = source.StartsWith("embedded:", StringComparison.Ordinal)
            ? source["embedded:".Length..]
            : source;

        return Path.GetFileNameWithoutExtension(value);
    }

    private static Stream OpenEmbeddedProfile(string resourceName) =>
        typeof(Program).Assembly.GetManifestResourceStream(resourceName)
        ?? throw new FileNotFoundException($"Встроенный профиль не найден: {resourceName}");

    private static List<string> Validate(IEnumerable<string>? values, string family)
    {
        var result = new List<string>();
        foreach (var value in values ?? [])
        {
            if (!IPAddress.TryParse(value, out var address) ||
                (family == "IPv4" && address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) ||
                (family == "IPv6" && address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6))
            {
                throw new InvalidDataException($"Некорректный адрес {family}: {value}");
            }

            result.Add(address.ToString());
        }

        return result;
    }

    private static string ResolveProfile(string profile, string directory)
    {
        var path = Path.IsPathRooted(profile)
            ? profile
            : Path.Combine(directory, profile.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? profile : profile + ".json");

        if (!File.Exists(path))
        {
            var embedded = ProfileSources(directory).FirstOrDefault(x =>
                ProfileName(x).Equals(Path.GetFileNameWithoutExtension(profile), StringComparison.OrdinalIgnoreCase));

            if (embedded is not null)
                return embedded;

            throw new FileNotFoundException($"Профиль не найден: {path}");
        }

        return path;
    }

    private static void RequireAdministrator()
    {
        if (!IsAdministrator())
            throw new InvalidOperationException("Для изменения DNS запустите терминал от имени администратора.");
    }

    private static bool IsAdministrator() =>
        OperatingSystem.IsWindows() &&
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    private static int RelaunchAsAdministrator(string[] args)
    {
        try
        {
            var processPath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Не удалось определить путь к программе.");

            var startInfo = new ProcessStartInfo(processPath)
            {
                UseShellExecute = true,
                Verb = "runas"
            };

            var childArguments = new List<string>();

            if (Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                childArguments.Add(Environment.GetCommandLineArgs()[0]);

            childArguments.AddRange(args);
            startInfo.Arguments = string.Join(" ", childArguments.Select(QuoteArgument));

            using var elevated = Process.Start(startInfo);
            elevated?.WaitForExit();
            return elevated?.ExitCode ?? 1;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Console.Error.WriteLine("Запуск отменён: для работы DNSCTL нужны права администратора.");
            return 1;
        }
    }

    private static string QuoteArgument(string argument) =>
        $"\"{argument.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"Ошибка: {message}");
        return 2;
    }

    private static void PrintHelp() => Console.WriteLine("""
        dnsctl — профили DNS для Windows

        Команды:
          dnsctl profiles [--config Config]              показать JSON-профили
          dnsctl list                                    показать сетевые адаптеры
          dnsctl status [--adapter "Wi-Fi"]             показать текущие DNS и статус DoH
          dnsctl apply <profile> [--adapter "Wi-Fi"]    применить профиль
          dnsctl reset [--adapter "Wi-Fi"]              вернуть DNS от DHCP

        Примеры:
          dnsctl apply dns_ai --adapter "Wi-Fi"
          dnsctl status --adapter "Wi-Fi"
          dnsctl reset
        """);
}

internal static class Ui
{
    public static void Header()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                         DNSCTL CLI                       ║");
        Console.WriteLine("║             управление DNS-профилями Windows             ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    public static void Line(string label, string value)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  {label,-10}: ");
        Console.ResetColor();
        Console.WriteLine(value);
    }

    public static void Separator()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ────────────────────────────────────────────────────────");
        Console.ResetColor();
    }

    public static void MenuItem(string key, string title, string hint) =>
        MenuItem(false, key, title, hint);

    public static void MenuItem(bool selected, string key, string title, string hint)
    {
        if (selected)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write("  ▶ ");
            Console.BackgroundColor = ConsoleColor.DarkCyan;
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"[{key}] {title,-30}");
            Console.ResetColor();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($" {hint}");
            Console.ResetColor();
        }
        else
        {
            Console.Write("    ");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"[{key}] ");
            Console.ResetColor();
            Console.Write($"{title,-30}");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($" {hint}");
            Console.ResetColor();
        }
    }

    public static bool IsUp(ConsoleKeyInfo k) =>
        k.Key == ConsoleKey.UpArrow || k.KeyChar is 'w' or 'W' or 'ц' or 'Ц';

    public static bool IsDown(ConsoleKeyInfo k) =>
        k.Key == ConsoleKey.DownArrow || k.KeyChar is 's' or 'S' or 'ы' or 'Ы';

    public static void Success(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n  ✓ {message}");
        Console.ResetColor();
    }

    public static void Warning(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n  ! {message}");
        Console.ResetColor();
    }

    public static void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ✗ {message}");
        Console.ResetColor();
    }

    public static void Pause()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("  Нажмите Enter, чтобы продолжить...");
        Console.ResetColor();
        Console.ReadLine();
    }
}

internal sealed class Options
{
    public string Command { get; private set; } = "";
    public string? Profile { get; private set; }
    public string? Adapter { get; private set; }
    public string ConfigDirectory { get; private set; } = "Config";
    public bool Help { get; private set; }
    public string? Error { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "-h" or "--help")
            {
                o.Help = true;
                continue;
            }

            if (args[i] is "--adapter" or "-a")
            {
                if (++i >= args.Length)
                {
                    o.Error = "После --adapter нужно указать имя адаптера.";
                    return o;
                }
                o.Adapter = args[i];
                continue;
            }

            if (args[i] is "--config" or "-c")
            {
                if (++i >= args.Length)
                {
                    o.Error = "После --config нужно указать папку.";
                    return o;
                }
                o.ConfigDirectory = args[i];
                continue;
            }

            if (args[i].StartsWith('-'))
            {
                o.Error = $"Неизвестный параметр: {args[i]}";
                return o;
            }

            positional.Add(args[i]);
        }

        if (o.Help)
            return o;

        if (positional.Count > 0)
            o.Command = positional[0].ToLowerInvariant();

        if (positional.Count > 1)
            o.Profile = positional[1];

        if (o.Command == "apply" && o.Profile is null)
            o.Error = "Для apply нужен профиль.";

        if (o.Command is not ("profiles" or "list" or "apply" or "reset" or "status"))
            o.Error = $"Неизвестная команда: {o.Command}";

        return o;
    }
}

internal sealed class DnsProfile
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Doh { get; set; }
    public List<string> Ipv4 { get; set; } = [];
    public List<string> Ipv6 { get; set; } = [];
}

internal sealed record Adapter(string Name, string Status, string Description);

internal static class Windows
{
    public static List<Adapter> GetAdapters()
    {
        var output = RunPowerShell("Get-NetAdapter | Select-Object Name,Status,InterfaceDescription | ConvertTo-Json -Compress");
        if (string.IsNullOrWhiteSpace(output))
            return [];

        using var doc = JsonDocument.Parse(output);
        var rows = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToArray()
            : [doc.RootElement];

        return rows.Select(x => new Adapter(
            x.GetProperty("Name").GetString() ?? "",
            x.GetProperty("Status").GetString() ?? "",
            x.GetProperty("InterfaceDescription").GetString() ?? ""
        )).ToList();
    }

    public static Adapter ResolveAdapter(string? name)
    {
        var adapters = GetAdapters();
        if (!string.IsNullOrWhiteSpace(name))
        {
            return adapters.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Адаптер не найден: {name}");
        }

        return adapters.FirstOrDefault(x =>
                   x.Status.Equals("Up", StringComparison.OrdinalIgnoreCase) &&
                   (x.Name.Contains("Wi", StringComparison.OrdinalIgnoreCase) || x.Description.Contains("Wireless", StringComparison.OrdinalIgnoreCase)))
               ?? adapters.FirstOrDefault(x => x.Status.Equals("Up", StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("Не найден активный сетевой адаптер.");
    }

    public static void SetDns(string adapter, IReadOnlyList<string> ipv4, IReadOnlyList<string> ipv6, string? dohTemplate)
    {
        var addresses = ipv4.Concat(ipv6).Select(PowerShellLiteral).ToArray();
        var addressList = string.Join(",", addresses);
        var alias = PowerShellLiteral(adapter);

        var script = $"$alias={alias}; $addresses=@({addressList}); Set-DnsClientServerAddress -InterfaceAlias $alias -ServerAddresses $addresses -Validate:$false;";

        if (!string.IsNullOrWhiteSpace(dohTemplate))
        {
            var template = PowerShellLiteral(dohTemplate);
            script += $" $template={template};" +
                " if ($null -eq (Get-Command Add-DnsClientDohServerAddress -ErrorAction SilentlyContinue)) {" +
                "   throw 'Эта версия Windows не поддерживает системный DoH через DNS Client.';" +
                " };" +
                " foreach ($server in $addresses) {" +
                "   $known = Get-DnsClientDohServerAddress -ServerAddress $server -ErrorAction SilentlyContinue;" +
                "   if ($null -eq $known) {" +
                "     Add-DnsClientDohServerAddress -ServerAddress $server -DohTemplate $template -AllowFallbackToUdp $false -AutoUpgrade $true -ErrorAction Stop;" +
                "   } else {" +
                "     Set-DnsClientDohServerAddress -ServerAddress $server -DohTemplate $template -AllowFallbackToUdp $false -AutoUpgrade $true -ErrorAction Stop;" +
                "   };" +
                "   $check = Get-DnsClientDohServerAddress -ServerAddress $server -ErrorAction Stop;" +
                "   if ($null -eq ($check | Where-Object { $_.DohTemplate -eq $template -and $_.AutoUpgrade -eq $true -and $_.AllowFallbackToUdp -eq $false })) {" +
                "     throw \"Windows не подтвердил DoH для DNS-сервера $server.\";" +
                "   };" +
                " };" +
                " try { netsh dns set global doh=auto | Out-Null } catch { };";
        }

        script += " Clear-DnsClientCache;";
        RunPowerShell(script);
    }

    public static void ResetDns(string adapter)
    {
        RunPowerShell($"Set-DnsClientServerAddress -InterfaceAlias {PowerShellLiteral(adapter)} -ResetServerAddresses; Clear-DnsClientCache;");
    }

    public static void ShowStatus(string? adapterName)
    {
        var adapter = ResolveAdapter(adapterName);
        Console.WriteLine($"\nАдаптер: {adapter.Name} ({adapter.Description})");

        var script = $"$alias = {PowerShellLiteral(adapter.Name)}; " +
            "$servers = (Get-DnsClientServerAddress -InterfaceAlias $alias).ServerAddresses; " +
            "if ($servers.Count -eq 0) { " +
            "  Write-Output 'DHCP'; " +
            "} else { " +
            "  $dohList = Get-DnsClientDohServerAddress -ErrorAction SilentlyContinue; " +
            "  $(foreach ($s in $servers) { " +
            "    $d = $dohList | Where-Object { $_.ServerAddress -eq $s }; " +
            "    [PSCustomObject]@{ " +
            "      Server = $s; " +
            "      DoH = ($null -ne $d); " +
            "      AutoUpgrade = if ($d) { $d.AutoUpgrade } else { $false }; " +
            "      Template = if ($d) { $d.DohTemplate } else { '-' }; " +
            "    } " +
            "  }) | ConvertTo-Json -Compress " +
            "}";

        var output = RunPowerShell(script).Trim();
        if (output == "DHCP" || string.IsNullOrWhiteSpace(output))
        {
            Console.WriteLine("  DNS: Получается автоматически (DHCP)");
            return;
        }

        Console.WriteLine("  Настроенные DNS-серверы и статус DoH:");
        using var doc = JsonDocument.Parse(output);
        var rows = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToArray()
            : [doc.RootElement];

        foreach (var row in rows)
        {
            var server = row.GetProperty("Server").GetString();
            var hasDoh = row.GetProperty("DoH").GetBoolean();
            var autoUpgrade = row.GetProperty("AutoUpgrade").GetBoolean();
            var tmpl = row.GetProperty("Template").GetString();
            var status = hasDoh && autoUpgrade ? "[DoH Активен]" : hasDoh ? "[DoH Зарегистрирован]" : "[Обычный DNS]";
            Console.WriteLine($"    {server,-24} {status,-22} {tmpl}");
        }
    }

    private static string RunPowerShell(string command) =>
        Run("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference='Stop'; " + command]);

    private static string PowerShellLiteral(string value) =>
        $"'{value.Replace("'", "''")}'";

    private static string Run(string file, IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Не удалось запустить {file}.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error) ? $"Команда {file} завершилась с ошибкой." : error.Trim());
        }

        return output;
    }
}
