namespace NektoMe.Infrastructure.Transports;

public sealed record VoiceDeviceInfo(
    string Manufacturer,
    string Model,
    string Build,
    string AndroidVersion,
    string UserAgent);

public sealed record VoiceProfile(
    VoiceDeviceInfo Device,
    string Store,          // "google" or "huawei"
    string UserId,         // $"{Store}-{Guid:D}"
    string Locale,         // "ru", "uk", "en"
    string TimeZone,       // e.g. "Europe/Kyiv"
    string EndpointPath);  // "/socket" or "/androiduk"

/// <summary>
/// Comprehensive pool of realistic modern Android devices, build fingerprints, and User-Agents
/// mirroring actual mobile clients of NektoMe Audio (version 1.7.1 / versionCode 96).
/// Includes authentic models widely used in CIS / Eastern Europe regions.
/// </summary>
public static class VoiceDevicePool
{
    private static readonly (string Manufacturer, string Model, string Build, string AndroidVersion)[] Devices =
    [
        // Samsung S-series (Flagships)
        ("Samsung", "SM-S928B", "UP1A.240305.011", "14"), // Galaxy S24 Ultra
        ("Samsung", "SM-S926B", "UP1A.240305.011", "14"), // Galaxy S24+
        ("Samsung", "SM-S921B", "UP1A.240305.011", "14"), // Galaxy S24
        ("Samsung", "SM-S918B", "UP1A.231005.007", "14"), // Galaxy S23 Ultra
        ("Samsung", "SM-S916B", "UP1A.231005.007", "14"), // Galaxy S23+
        ("Samsung", "SM-S911B", "UP1A.231005.007", "14"), // Galaxy S23
        ("Samsung", "SM-S908B", "UP1A.231005.007", "14"), // Galaxy S22 Ultra
        ("Samsung", "SM-S901B", "UP1A.231005.007", "14"), // Galaxy S22
        ("Samsung", "SM-G998B", "TP1A.220624.014", "13"), // Galaxy S21 Ultra
        ("Samsung", "SM-G991B", "TP1A.220624.014", "13"), // Galaxy S21
        ("Samsung", "SM-G990B", "UP1A.231005.007", "14"), // Galaxy S21 FE
        ("Samsung", "SM-F731B", "UP1A.231005.007", "14"), // Galaxy Z Flip5
        ("Samsung", "SM-F946B", "UP1A.231005.007", "14"), // Galaxy Z Fold5

        // Samsung A-series & M-series (High volume mid-range)
        ("Samsung", "SM-A556B", "UP1A.240305.011", "14"), // Galaxy A55 5G
        ("Samsung", "SM-A546B", "UP1A.231005.007", "14"), // Galaxy A54 5G
        ("Samsung", "SM-A536B", "TP1A.220624.014", "13"), // Galaxy A53 5G
        ("Samsung", "SM-A528B", "TP1A.220624.014", "13"), // Galaxy A52s 5G
        ("Samsung", "SM-A356B", "UP1A.240305.011", "14"), // Galaxy A35 5G
        ("Samsung", "SM-A346B", "UP1A.231005.007", "14"), // Galaxy A34 5G
        ("Samsung", "SM-A256B", "UP1A.240305.011", "14"), // Galaxy A25 5G
        ("Samsung", "SM-A155F", "UP1A.231005.007", "14"), // Galaxy A15
        ("Samsung", "SM-A145F", "UP1A.231005.007", "14"), // Galaxy A14
        ("Samsung", "SM-M346B", "UP1A.231005.007", "14"), // Galaxy M34 5G

        // Google Pixel
        ("Google", "Pixel 8 Pro", "UQ1A.240205.002", "14"),
        ("Google", "Pixel 8", "UQ1A.240205.002", "14"),
        ("Google", "Pixel 7 Pro", "UQ1A.240105.004", "14"),
        ("Google", "Pixel 7a", "UQ1A.240205.002", "14"),
        ("Google", "Pixel 7", "TQ3A.230805.001", "13"),
        ("Google", "Pixel 6 Pro", "TQ3A.230901.001", "13"),
        ("Google", "Pixel 6a", "TQ3A.230901.001", "13"),

        // Xiaomi & Redmi & POCO
        ("Xiaomi", "23127PN0CG", "UKQ1.230804.001", "14"), // Xiaomi 14
        ("Xiaomi", "24030PN60G", "UKQ1.231008.001", "14"), // Xiaomi 14 Ultra
        ("Xiaomi", "23078PND5G", "UKQ1.230804.001", "14"), // Xiaomi 13T Pro
        ("Xiaomi", "2306EPN60G", "UKQ1.230804.001", "14"), // Xiaomi 13T
        ("Xiaomi", "2210132G", "TKQ1.220829.002", "13"),   // Xiaomi 13 Pro
        ("Xiaomi", "2211133G", "TKQ1.221114.001", "13"),   // Xiaomi 13
        ("Xiaomi", "22081212UG", "TKQ1.220829.002", "13"), // Xiaomi 12T Pro
        ("Xiaomi", "2311DRK48G", "UKQ1.230917.001", "14"), // POCO X6 Pro 5G
        ("Xiaomi", "23122PCD1G", "UKQ1.230917.001", "14"), // POCO X6 5G
        ("Xiaomi", "23049PCD8G", "UKQ1.230804.001", "14"), // POCO F5
        ("Xiaomi", "23013PC75G", "TKQ1.221114.001", "13"), // POCO F5 Pro
        ("Xiaomi", "22101320G", "TKQ1.220829.002", "13"),  // POCO X5 Pro 5G
        ("Xiaomi", "2312FPCA6G", "UKQ1.230917.001", "14"), // POCO M6 Pro
        ("Xiaomi", "2312DRA50G", "UKQ1.230917.001", "14"), // Redmi Note 13 Pro+ 5G
        ("Xiaomi", "23117RA68G", "UKQ1.230917.001", "14"), // Redmi Note 13 Pro 4G
        ("Xiaomi", "23129RAA4G", "TP1A.220624.014", "13"), // Redmi Note 13 4G
        ("Xiaomi", "22101316G", "TP1A.220624.014", "13"),  // Redmi Note 12 Pro
        ("Xiaomi", "23021RAAEG", "TP1A.220624.014", "13"), // Redmi Note 12 4G
        ("Xiaomi", "23053RN02Y", "TP1A.220624.014", "13"), // Redmi 12
        ("Xiaomi", "220333QAG", "SKQ1.211103.001", "12"),  // Redmi 10C

        // OnePlus
        ("OnePlus", "CPH2581", "UKQ1.230924.001", "14"), // OnePlus 12
        ("OnePlus", "CPH2449", "UKQ1.230804.001", "14"), // OnePlus 11
        ("OnePlus", "CPH2413", "TP1A.220905.001", "13"), // OnePlus 10 Pro
        ("OnePlus", "CPH2493", "TP1A.220905.001", "13"), // OnePlus Nord 3 5G
        ("OnePlus", "CPH2513", "TP1A.220905.001", "13"), // OnePlus Nord CE 3 Lite 5G

        // Realme
        ("Realme", "RMX3840", "UKQ1.230924.001", "14"), // Realme 12 Pro+
        ("Realme", "RMX3771", "UKQ1.230924.001", "14"), // Realme 11 Pro+ 5G
        ("Realme", "RMX3709", "TP1A.220905.001", "13"), // Realme GT 3
        ("Realme", "RMX3706", "TP1A.220905.001", "13"), // Realme GT Neo 5
        ("Realme", "RMX3630", "TP1A.220905.001", "13"), // Realme 10
        ("Realme", "RMX3710", "TP1A.220905.001", "13"), // Realme C55

        // Honor & Huawei (High market share in CIS)
        ("Honor", "REA-NX9", "UKQ1.230924.001", "14"), // Honor 90
        ("Honor", "BVH-N09", "UKQ1.230924.001", "14"), // Honor Magic 6 Pro
        ("Honor", "PGT-N19", "TP1A.220905.001", "13"), // Honor Magic 5 Pro
        ("Honor", "ALI-NX1", "TP1A.220905.001", "13"), // Honor X9b
        ("Honor", "CRT-LX1", "TP1A.220905.001", "13"), // Honor X8a
        ("HUAWEI", "NOH-NX9", "TP1A.220624.014", "12"), // HUAWEI Mate 40 Pro

        // Vivo / iQOO
        ("Vivo", "V2318", "UKQ1.230924.001", "14"),   // Vivo V30 5G
        ("Vivo", "V2250", "TP1A.220905.001", "13"),   // Vivo V29 5G
        ("Vivo", "V2247", "TP1A.220905.001", "13"),   // Vivo Y36
        ("Vivo", "V2339FA", "UKQ1.230924.001", "14"), // iQOO Neo 9
        ("Vivo", "V2307A", "UKQ1.230924.001", "14"),  // iQOO 12

        // Oppo
        ("OPPO", "CPH2499", "UKQ1.230924.001", "14"), // Oppo Find N3
        ("OPPO", "CPH2525", "UKQ1.230924.001", "14"), // Oppo Reno 10 Pro 5G
        ("OPPO", "CPH2565", "TP1A.220905.001", "13"), // Oppo A78

        // Tecno & Infinix
        ("TECNO", "CK8n", "TP1A.220624.014", "13"),      // Tecno Camon 20 Pro 5G
        ("TECNO", "KI7", "TP1A.220624.014", "13"),       // Tecno Spark 10 Pro
        ("Infinix", "X6710", "TP1A.220624.014", "13"),   // Infinix Note 30 VIP
        ("Infinix", "X669", "TP1A.220624.014", "13"),    // Infinix Hot 30

        // Motorola
        ("Motorola", "moto g84 5G", "T3SC33.16-56-7", "13"),
        ("Motorola", "moto g54 5G", "T1TC33.1-66-4", "13"),
        ("Motorola", "motorola edge 40 neo", "T2TM33.6-28-5", "13")
    ];

    private static readonly string[] CompatibleTimezones =
    [
        "Europe/Kyiv",
        "Europe/Warsaw",
        "Europe/Bucharest",
        "Europe/Chisinau",
        "Europe/Prague",
        "Europe/Sofia",
        "Europe/Vilnius",
        "Europe/Riga",
        "Europe/Tallinn",
        "Europe/Minsk",
        "Europe/Moscow",
        "Asia/Almaty",
        "Asia/Tashkent",
        "Asia/Tbilisi",
        "Asia/Yerevan"
    ];

    private static readonly string[] CompatibleLocales =
    [
        "ru",
        "uk",
        "en"
    ];

    private static readonly string[] EndpointPaths =
    [
        "/androiduk"
    ];

    public static VoiceDeviceInfo GetRandomDevice(int versionCode = 96)
    {
        var (manuf, model, build, android) = Devices[Random.Shared.Next(Devices.Length)];
        string ua = $"NektoMeAudio{versionCode}/2.1.0 (Linux; U; Android {android}; {model} Build/{build})";
        return new VoiceDeviceInfo(manuf, model, build, android, ua);
    }

    public static string GetRandomTimezone() =>
        CompatibleTimezones[Random.Shared.Next(CompatibleTimezones.Length)];

    public static string GetRandomLocale() =>
        CompatibleLocales[Random.Shared.Next(CompatibleLocales.Length)];

    public static string GetRandomStore() =>
        Random.Shared.Next(4) == 0 ? "huawei" : "google";

    public static string GetRandomEndpointPath() =>
        EndpointPaths[Random.Shared.Next(EndpointPaths.Length)];

    public static VoiceProfile GenerateRandomProfile(int versionCode = 96)
    {
        VoiceDeviceInfo device = GetRandomDevice(versionCode);
        string store = (device.Manufacturer is "HUAWEI" || device.Manufacturer is "Honor")
            ? "huawei"
            : "google";
        string userId = $"{store}-{Guid.NewGuid():D}";
        string locale = GetRandomLocale();
        string timeZone = GetRandomTimezone();
        string path = "/androiduk"; // Official default in APK (Remote Config)

        return new VoiceProfile(device, store, userId, locale, timeZone, path);
    }
}
