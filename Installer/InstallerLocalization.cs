using System.Globalization;
using System.Runtime.InteropServices;

namespace MonitorAndControl.Installer;

/// <summary>
/// Detects the Windows OS UI language and provides localized messages for the installer.
/// Supported languages: English (en), Spanish (es), German (de), French (fr), Russian (ru).
/// If the detected language is not supported, English (en) is used as fallback.
/// </summary>
public static class InstallerLocalization
{
    private static string? _language;

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetUserDefaultUILanguage();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern ushort GetSystemDefaultUILanguage();

    /// <summary>
    /// Gets or sets the active language code ("en", "es", "de", "fr", "ru").
    /// </summary>
    public static string Language
    {
        get => _language ??= DetectLanguage();
        set => _language = NormalizeLanguage(value);
    }

    /// <summary>
    /// Normalizes any language tag or locale string to a supported 2-letter language code,
    /// falling back to "en".
    /// </summary>
    public static string NormalizeLanguage(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return "en";
        var trimmed = lang.Trim().ToLowerInvariant();
        if (trimmed.Length >= 2)
        {
            var prefix = trimmed[..2];
            if (prefix is "es" or "de" or "fr" or "ru") return prefix;
        }
        return "en";
    }

    /// <summary>
    /// Detects the OS user interface language using culture information and Win32 APIs.
    /// </summary>
    public static string DetectLanguage()
    {
        try
        {
            // 1. Thread UI culture
            var threadUi = CultureInfo.CurrentUICulture?.TwoLetterISOLanguageName;
            var norm = NormalizeLanguage(threadUi);
            if (norm != "en") return norm;
            if (threadUi != null && threadUi.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";

            // 2. Installed OS UI culture
            var installedUi = CultureInfo.InstalledUICulture?.TwoLetterISOLanguageName;
            norm = NormalizeLanguage(installedUi);
            if (norm != "en") return norm;
            if (installedUi != null && installedUi.StartsWith("en", StringComparison.OrdinalIgnoreCase)) return "en";

            // 3. User default UI language (Win32)
            ushort userLang = GetUserDefaultUILanguage();
            norm = LanguageFromLangId(userLang);
            if (norm != "en") return norm;

            // 4. System default UI language (Win32)
            ushort sysLang = GetSystemDefaultUILanguage();
            norm = LanguageFromLangId(sysLang);
            if (norm != "en") return norm;
        }
        catch
        {
            // Fallback to English on error
        }
        return "en";
    }

    /// <summary>
    /// Maps a Win32 LANGID to one of the supported language codes.
    /// </summary>
    public static string LanguageFromLangId(ushort langId)
    {
        int primary = langId & 0x3ff;
        return primary switch
        {
            0x0a => "es",
            0x07 => "de",
            0x0c => "fr",
            0x19 => "ru",
            _ => "en"
        };
    }

    // --- Window Titles & Common Buttons ---

    public static string SetupTitle(string version) =>
        $"Monitor & Control Setup {version}";

    public static string ButtonClose =>
        Language switch
        {
            "es" => "Cerrar",
            "de" => "Schließen",
            "fr" => "Fermer",
            "ru" => "Закрыть",
            _ => "Close"
        };

    public static string ButtonContinue =>
        Language switch
        {
            "es" => "Continuar",
            "de" => "Weiter",
            "fr" => "Continuer",
            "ru" => "Продолжить",
            _ => "Continue"
        };

    // --- Elevation & Permissions ---

    public static string ElevationRequiredMessage =>
        Language switch
        {
            "es" => "Monitor & Control Setup necesita permisos de administrador para instalar el servicio GameHost.\n\nEjecútalo de nuevo y selecciona Sí en el aviso de Windows.",
            "de" => "Monitor & Control Setup benötigt Administratorrechte, um den GameHost-Dienst zu installieren.\n\nFühren Sie es erneut aus und wählen Sie Ja bei der Windows-Eingabeaufforderung.",
            "fr" => "Monitor & Control Setup nécessite des droits d'administrateur pour installer le service GameHost.\n\nExécutez-le à nouveau et sélectionnez Oui à l'invite Windows.",
            "ru" => "Monitor & Control Setup требуются права администратора для установки службы GameHost.\n\nЗапустите программу снова и выберите «Да» в запросе Windows.",
            _ => "Monitor & Control Setup needs administrator rights to install the GameHost service.\n\nRun it again and choose Yes at the Windows prompt."
        };

    public static string RunningAsAdmin(string user) =>
        Language switch
        {
            "es" => $"Ejecutando como {user} con permisos de administrador.",
            "de" => $"Wird als {user} mit Administratorrechten ausgeführt.",
            "fr" => $"Exécution en tant que {user} avec des droits d'administrateur.",
            "ru" => $"Запуск от имени {user} с правами администратора.",
            _ => $"Running as {user} with administrator rights."
        };

    // --- Status Lines ---

    public static string Starting =>
        Language switch
        {
            "es" => "Iniciando...",
            "de" => "Wird gestartet...",
            "fr" => "Démarrage...",
            "ru" => "Запуск...",
            _ => "Starting..."
        };

    public static string ExtractingFiles =>
        Language switch
        {
            "es" => "Extrayendo archivos...",
            "de" => "Dateien werden extrahiert...",
            "fr" => "Extraction des fichiers...",
            "ru" => "Извлечение файлов...",
            _ => "Extracting files..."
        };

    public static string InstallingMonitorAndControl =>
        Language switch
        {
            "es" => "Instalando Monitor & Control...",
            "de" => "Monitor & Control wird installiert...",
            "fr" => "Installation de Monitor & Control...",
            "ru" => "Установка Monitor & Control...",
            _ => "Installing Monitor & Control..."
        };

    public static string InstalledSuccess(string version) =>
        Language switch
        {
            "es" => $"Monitor & Control {version} está instalado.",
            "de" => $"Monitor & Control {version} ist installiert.",
            "fr" => $"Monitor & Control {version} est installé.",
            "ru" => $"Monitor & Control {version} установлен.",
            _ => $"Monitor & Control {version} is installed."
        };

    public static string SetupFailed =>
        Language switch
        {
            "es" => "La instalación falló.",
            "de" => "Installation fehlgeschlagen.",
            "fr" => "L'installation a échoué.",
            "ru" => "Сбой установки.",
            _ => "Setup failed."
        };

    public static string VerifyingRuntime(string name) =>
        Language switch
        {
            "es" => $"Verificando {name}...",
            "de" => $"{name} wird überprüft...",
            "fr" => $"Vérification de {name}...",
            "ru" => $"Проверка {name}...",
            _ => $"Verifying {name}..."
        };

    public static string InstallingRuntimeProgress(string name, string elapsed) =>
        Language switch
        {
            "es" => $"Instalando {name}... {elapsed} transcurridos",
            "de" => $"{name} wird installiert... {elapsed} vergangen",
            "fr" => $"Installation de {name}... {elapsed} écoulé",
            "ru" => $"Установка {name}... прошло {elapsed}",
            _ => $"Installing {name}... {elapsed} elapsed"
        };

    public static string DownloadingRuntime(string name) =>
        Language switch
        {
            "es" => $"Descargando {name}...",
            "de" => $"{name} wird heruntergeladen...",
            "fr" => $"Téléchargement de {name}...",
            "ru" => $"Скачивание {name}...",
            _ => $"Downloading {name}..."
        };

    public static string DownloadingRuntimeProgress(string name, double doneMb, double totalMb) =>
        Language switch
        {
            "es" => $"Descargando {name}... {doneMb:0.0} de {totalMb:0.0} MB",
            "de" => $"{name} wird heruntergeladen... {doneMb:0.0} von {totalMb:0.0} MB",
            "fr" => $"Téléchargement de {name}... {doneMb:0.0} sur {totalMb:0.0} Mo",
            "ru" => $"Скачивание {name}... {doneMb:0.0} из {totalMb:0.0} МБ",
            _ => $"Downloading {name}... {doneMb:0.0} of {totalMb:0.0} MB"
        };

    // --- Log Messages ---

    public static string RuntimeNotInstalled(string name) =>
        Language switch
        {
            "es" => $"{name} no está instalado.",
            "de" => $"{name} ist nicht installiert.",
            "fr" => $"{name} n'est pas installé.",
            "ru" => $"{name} не установлен.",
            _ => $"{name} is not installed."
        };

    public static string InstallingRuntimePrompt(string name) =>
        Language switch
        {
            "es" => $"Instalando {name} (el instalador de Microsoft muestra su propia ventana de progreso)...",
            "de" => $"{name} wird installiert (das Microsoft-Installationsprogramm zeigt ein eigenes Fortschrittsfenster)...",
            "fr" => $"Installation de {name} (le programme d'installation de Microsoft affiche sa propre fenêtre de progression)...",
            "ru" => $"Установка {name} (установщик Microsoft отображает собственное окно выполнения)...",
            _ => $"Installing {name} (Microsoft's installer shows its own progress window)..."
        };

    public static string RuntimeInstalled(string name) =>
        Language switch
        {
            "es" => $"{name} instalado.",
            "de" => $"{name} installiert.",
            "fr" => $"{name} installé.",
            "ru" => $"{name} установлен.",
            _ => $"{name} installed."
        };

    public static string VersionToInstall(string version) =>
        Language switch
        {
            "es" => $"Versión a instalar: {version}",
            "de" => $"Zu installierende Version: {version}",
            "fr" => $"Version à installer : {version}",
            "ru" => $"Версия для установки: {version}",
            _ => $"Version to install: {version}"
        };

    public static string VersionOnDiskNone =>
        Language switch
        {
            "es" => "Versión en disco: ninguna (no se encontró una instalación previa).",
            "de" => "Version auf dem Datenträger: keine (keine vorherige Installation gefunden).",
            "fr" => "Version sur le disque : aucune (aucune installation précédente trouvée).",
            "ru" => "Версия на диске: отсутствует (предыдущая установка не найдена).",
            _ => "Version on disk: none (no previous installation found)."
        };

    public static string VersionOnDiskFound(string version, string dir) =>
        Language switch
        {
            "es" => $"Versión en disco: {version} (en {dir}).",
            "de" => $"Version auf dem Datenträger: {version} (in {dir}).",
            "fr" => $"Version sur le disque : {version} (dans {dir}).",
            "ru" => $"Версия на диске: {version} (в {dir}).",
            _ => $"Version on disk: {version} (in {dir})."
        };

    public static string SameVersionReinstall =>
        Language switch
        {
            "es" => "Misma versión: los archivos se reinstalarán.",
            "de" => "Gleiche Version: Die Dateien werden neu installiert.",
            "fr" => "Même version : les fichiers seront réinstallés.",
            "ru" => "Та же версия: файлы будут переустановлены.",
            _ => "Same version: the files will be reinstalled."
        };

    public static string UpgradingVersion(string oldVer, string newVer) =>
        Language switch
        {
            "es" => $"Actualizando de {oldVer} a {newVer}.",
            "de" => $"Aktualisierung von {oldVer} auf {newVer}.",
            "fr" => $"Mise à niveau de {oldVer} vers {newVer}.",
            "ru" => $"Обновление с {oldVer} до {newVer}.",
            _ => $"Upgrading from {oldVer} to {newVer}."
        };

    public static string DowngradingVersion(string oldVer, string newVer) =>
        Language switch
        {
            "es" => $"La versión en disco es más reciente: será reemplazada por la versión anterior {newVer} (desactualización).",
            "de" => $"Die Version auf dem Datenträger ist neuer: Sie wird durch die ältere Version {newVer} ersetzt (Downgrade).",
            "fr" => $"La version sur le disque est plus récente : elle sera remplacée par l'ancienne version {newVer} (rétrogradation).",
            "ru" => $"Версия на диске новее: она будет заменена более старой версией {newVer} (откат).",
            _ => $"The version on disk is newer: it will be replaced by the older {newVer} (downgrade)."
        };

    public static string PreviousInstallReplaced(string prevDir) =>
        Language switch
        {
            "es" => $"La instalación previa en {prevDir} se ha reemplazado por la nueva. Sus archivos se han conservado; puedes eliminar esa carpeta cuando todo funcione.",
            "de" => $"Die vorherige Installation in {prevDir} wird durch die neue ersetzt. Die Dateien blieben erhalten; Sie können diesen Ordner löschen, sobald alles funktioniert.",
            "fr" => $"L'ancienne installation dans {prevDir} est remplacée par la nouvelle. Ses fichiers sont conservés ; vous pourrez supprimer ce dossier lorsque tout fonctionnera.",
            "ru" => $"Предыдущая установка в {prevDir} заменена новой. Её файлы остались на месте; вы можете удалить эту папку, когда всё заработает.",
            _ => $"The previous installation in {prevDir} is replaced by the new one. Its files were left in place; you can delete that folder once everything works."
        };

    public static string InstalledVersionLog(string version) =>
        Language switch
        {
            "es" => $"Versión instalada: {version}.",
            "de" => $"Installierte Version: {version}.",
            "fr" => $"Version installée : {version}.",
            "ru" => $"Установленная версия: {version}.",
            _ => $"Installed version: {version}."
        };

    public static string SetupFinished =>
        Language switch
        {
            "es" => "Instalación finalizada.",
            "de" => "Installation abgeschlossen.",
            "fr" => "Installation terminée.",
            "ru" => "Установка завершена.",
            _ => "Setup finished."
        };

    // --- Errors & Exceptions ---

    public static string InvalidAppSettings(string val) =>
        Language switch
        {
            "es" => $"Valor no válido para -AppSettings '{val}'. Usa Merge, Keep o Replace.",
            "de" => $"Ungültiger Wert für -AppSettings '{val}'. Verwenden Sie Merge, Keep oder Replace.",
            "fr" => $"Valeur -AppSettings non valide '{val}'. Utilisez Merge, Keep ou Replace.",
            "ru" => $"Недопустимое значение -AppSettings «{val}». Используйте Merge, Keep или Replace.",
            _ => $"Invalid -AppSettings value '{val}'. Use Merge, Keep or Replace."
        };

    public static string InstallScriptFailed(int code) =>
        Language switch
        {
            "es" => $"install.ps1 falló (código de salida {code}).",
            "de" => $"install.ps1 fehlgeschlagen (Exit-Code {code}).",
            "fr" => $"install.ps1 a échoué (code de sortie {code}).",
            "ru" => $"install.ps1 завершился с ошибкой (код выхода {code}).",
            _ => $"install.ps1 failed (exit code {code})."
        };

    public static string DownloadNotSigned =>
        Language switch
        {
            "es" => "la descarga no está firmada por Microsoft",
            "de" => "der Download ist nicht von Microsoft signiert",
            "fr" => "le téléchargement n'est pas signé par Microsoft",
            "ru" => "загруженный файл не подписан Microsoft",
            _ => "the download is not signed by Microsoft"
        };

    public static string CouldNotStartRuntimeInstaller =>
        Language switch
        {
            "es" => "no se pudo iniciar el instalador del entorno en tiempo de ejecución",
            "de" => "das Runtime-Installationsprogramm konnte nicht gestartet werden",
            "fr" => "impossible de démarrer le programme d'installation du runtime",
            "ru" => "не удалось запустить установщик среды выполнения",
            _ => "could not start the runtime installer"
        };

    public static string RuntimeInstallerTimeout =>
        Language switch
        {
            "es" => "el instalador del entorno en tiempo de ejecución no finalizó en 20 minutos",
            "de" => "das Runtime-Installationsprogramm wurde nicht innerhalb von 20 Minuten abgeschlossen",
            "fr" => "le programme d'installation du runtime ne s'est pas terminé en 20 minutes",
            "ru" => "установщик среды выполнения не завершил работу за 20 минут",
            _ => "the runtime installer did not finish in 20 minutes"
        };

    public static string RuntimeInstallerExitCode(int code) =>
        Language switch
        {
            "es" => $"el instalador del entorno en tiempo de ejecución devolvió el código de salida {code}",
            "de" => $"das Runtime-Installationsprogramm hat den Exit-Code {code} zurückgegeben",
            "fr" => $"le programme d'installation du runtime a renvoyé le code de sortie {code}",
            "ru" => $"установщик среды выполнения вернул код выхода {code}",
            _ => $"the runtime installer returned exit code {code}"
        };

    public static string RuntimeInstallFailed(string name, string message, string logFile) =>
        Language switch
        {
            "es" => $"No se pudo instalar {name} ({message}). Instálalo desde https://dotnet.microsoft.com/download/dotnet/8.0 y ejecuta la instalación de nuevo. Registro del instalador: {logFile}",
            "de" => $"{name} konnte nicht installiert werden ({message}). Installieren Sie es von https://dotnet.microsoft.com/download/dotnet/8.0 und führen Sie das Setup erneut aus. Protokoll: {logFile}",
            "fr" => $"Impossible d'installer {name} ({message}). Installez-le depuis https://dotnet.microsoft.com/download/dotnet/8.0 et relancez l'installation. Journal : {logFile}",
            "ru" => $"Не удалось установить {name} ({message}). Установите его с https://dotnet.microsoft.com/download/dotnet/8.0 и запустите установку снова. Журнал: {logFile}",
            _ => $"Could not install the {name} ({message}). Install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run setup again. Runtime installer log: {logFile}"
        };

    public static string NoPayload =>
        Language switch
        {
            "es" => "Este archivo de instalación no contiene ningún paquete. Constrúyelo con publish.ps1.",
            "de" => "Diese Setup-Datei enthält keine Nutzdaten. Erstellen Sie sie mit publish.ps1.",
            "fr" => "Ce fichier d'installation ne contient aucune charge utile. Construisez-le avec publish.ps1.",
            "ru" => "Этот файл установки не содержит полезной нагрузки. Соберите его с помощью publish.ps1.",
            _ => "This setup file contains no payload. Build it with publish.ps1."
        };

    public static string InstallScriptMissing =>
        Language switch
        {
            "es" => "install.ps1 no se encuentra en el paquete de instalación.",
            "de" => "install.ps1 fehlt in den Setup-Nutzdaten.",
            "fr" => "install.ps1 est manquant dans la charge utile d'installation.",
            "ru" => "install.ps1 отсутствует в полезной нагрузке установки.",
            _ => "install.ps1 is missing from the setup payload."
        };

    public static string CouldNotStartPowerShell =>
        Language switch
        {
            "es" => "No se pudo iniciar PowerShell.",
            "de" => "PowerShell konnte nicht gestartet werden.",
            "fr" => "Impossible de démarrer PowerShell.",
            "ru" => "Не удалось запустить PowerShell.",
            _ => "Could not start PowerShell."
        };

    // --- AppSettings Dialog ---

    public static string SettingsDialogTitle =>
        Language switch
        {
            "es" => "Monitor & Control Setup - nueva configuración",
            "de" => "Monitor & Control Setup – neue Einstellungen",
            "fr" => "Monitor & Control Setup - nouveaux paramètres",
            "ru" => "Monitor & Control Setup — новые настройки",
            _ => "Monitor & Control Setup - new settings"
        };

    public static string SettingsIntro =>
        Language switch
        {
            "es" => "Esta versión añade opciones que no están en tu appsettings.json:",
            "de" => "Diese Version fügt Einstellungen hinzu, die in Ihrer appsettings.json fehlen:",
            "fr" => "Cette version ajoute des paramètres manquants dans votre appsettings.json :",
            "ru" => "В этой версии добавлены настройки, отсутствующие в вашем appsettings.json:",
            _ => "This version adds settings that are missing from your appsettings.json:"
        };

    public static string SettingsAsk =>
        Language switch
        {
            "es" => "¿Qué debe hacer la instalación?",
            "de" => "Wie soll das Setup vorgehen?",
            "fr" => "Que doit faire l'installation ?",
            "ru" => "Что должен сделать установщик?",
            _ => "What should Setup do?"
        };

    public static string SettingsMergeOption =>
        Language switch
        {
            "es" => "Añadir las nuevas opciones con sus valores por defecto y mantener todas mis opciones actuales (recomendado)",
            "de" => "Die neuen Einstellungen mit Standardwerten hinzufügen und alle vorhandenen Werte beibehalten (empfohlen)",
            "fr" => "Ajouter les nouveaux paramètres avec leurs valeurs par défaut et conserver mes paramètres existants (recommandé)",
            "ru" => "Добавить новые параметры со значениями по умолчанию и сохранить все текущие настройки (рекомендуется)",
            _ => "Add the new settings with their default values and keep all my existing values (recommended)"
        };

    public static string SettingsKeepOption =>
        Language switch
        {
            "es" => "Mantener mi archivo tal cual (las nuevas opciones usarán los valores por defecto del programa)",
            "de" => "Datei unverändert beibehalten (die neuen Einstellungen nutzen die integrierten Standardwerte)",
            "fr" => "Conserver mon fichier tel quel (les nouveaux paramètres utiliseront les valeurs par défaut du programme)",
            "ru" => "Оставить файл без изменений (для новых параметров будут использоваться значения по умолчанию программы)",
            _ => "Keep my file exactly as it is (the new settings use the program's built-in defaults)"
        };

    public static string SettingsReplaceOption =>
        Language switch
        {
            "es" => "Reemplazarlo con el nuevo archivo por defecto (se guardará una copia de seguridad de mi archivo)",
            "de" => "Durch die neue Standarddatei ersetzen (vorherige Datei wird als Sicherung gespeichert)",
            "fr" => "Le remplacer par le nouveau fichier par défaut (mon fichier sera d'abord sauvegardé)",
            "ru" => "Заменить новым файлом по умолчанию (предварительно будет создана резервная копия моего файла)",
            _ => "Replace it with the new default file (my file is saved as a backup first)"
        };

    // --- AppSettings Migration Log Messages ---

    public static string NoExistingAppSettings =>
        Language switch
        {
            "es" => "No existe appsettings.json: se instalará la configuración por defecto.",
            "de" => "Keine vorhandene appsettings.json: Die Standardeinstellungen werden installiert.",
            "fr" => "Aucun fichier appsettings.json existant : les paramètres par défaut seront installés.",
            "ru" => "Файл appsettings.json не найден: будут установлены настройки по умолчанию.",
            _ => "No existing appsettings.json: the default settings will be installed."
        };

    public static string InvalidExistingJson(string message, bool imported) =>
        Language switch
        {
            "es" => $"El archivo appsettings.json existente no es un JSON válido ({message}); se ha dejado sin cambios" + (imported ? " y no se importó." : "."),
            "de" => $"Die vorhandene appsettings.json ist kein gültiges JSON ({message}); sie blieb unverändert" + (imported ? " und wurde nicht übernommen." : "."),
            "fr" => $"Le fichier appsettings.json existant n'est pas un JSON valide ({message}) ; il a été laissé intact" + (imported ? " et non importé." : "."),
            "ru" => $"Существующий appsettings.json не является корректным JSON ({message}); он оставлен без изменений" + (imported ? " и не был перенесён." : "."),
            _ => $"The existing appsettings.json is not valid JSON ({message}); it was left untouched" + (imported ? " and not carried over." : ".")
        };

    public static string MergedSettingsLog(bool imported, int count, string names) =>
        Language switch
        {
            "es" => (imported ? "Se importó la configuración de la instalación previa" : "Se mantuvo tu configuración") +
                    $" y se añadieron {count} nueva(s) opción(es): {names}.",
            "de" => (imported ? "Einstellungen der vorherigen Installation übernommen" : "Ihre Einstellungen wurden beibehalten") +
                    $" und {count} neue Option(en) hinzugefügt: {names}.",
            "fr" => (imported ? "Vos paramètres de l'installation précédente ont été repris" : "Vos paramètres ont été conservés") +
                    $" et {count} nouvelle(s) option(s) ont été ajoutée(s) : {names}.",
            "ru" => (imported ? "Настройки из предыдущей установки перенесены" : "Ваши настройки сохранены") +
                    $" и добавлено {count} новых параметров: {names}.",
            _ => (imported ? "Carried over your settings from the previous installation" : "Kept your settings") +
                 $" and added {count} new option(s): {names}."
        };

    public static string ReplacedSettingsBackupLog(string stamp) =>
        Language switch
        {
            "es" => $"Se reemplazó appsettings.json por el nuevo archivo predeterminado. Tu archivo anterior se guardó como appsettings.json.bak-{stamp}.",
            "de" => $"appsettings.json wurde durch die neue Standarddatei ersetzt. Ihre vorherige Datei wurde als appsettings.json.bak-{stamp} gesichert.",
            "fr" => $"appsettings.json a été remplacé par le nouveau fichier par défaut. Votre ancien fichier est sauvegardé sous appsettings.json.bak-{stamp}.",
            "ru" => $"Файл appsettings.json заменён новым файлом по умолчанию. Предыдущий файл сохранён как appsettings.json.bak-{stamp}.",
            _ => $"Replaced appsettings.json with the new default file. Your previous file is saved as appsettings.json.bak-{stamp}."
        };

    public static string ReplacedSettingsImportedLog =>
        Language switch
        {
            "es" => "No se importó la configuración de la instalación previa; se instalará el nuevo archivo predeterminado.",
            "de" => "Die Einstellungen der vorherigen Installation wurden nicht übernommen; die neue Standarddatei wird installiert.",
            "fr" => "Les paramètres de l'installation précédente n'ont pas été repris ; le nouveau fichier par défaut est installé.",
            "ru" => "Настройки предыдущей установки не перенесены; устанавливается новый файл по умолчанию.",
            _ => "The settings of the previous installation were not carried over; the new default file is installed."
        };

    public static string KeptSettingsImportedLog =>
        Language switch
        {
            "es" => "Se importó la configuración de la instalación previa.",
            "de" => "Einstellungen der vorherigen Installation wurden übernommen.",
            "fr" => "Vos paramètres de l'installation précédente ont été repris.",
            "ru" => "Настройки из предыдущей установки перенесены.",
            _ => "Carried over your settings from the previous installation."
        };

    public static string KeptSettingsExistingLog =>
        Language switch
        {
            "es" => "Se mantuvo tu appsettings.json actual.",
            "de" => "Ihre vorhandene appsettings.json wurde beibehalten.",
            "fr" => "Votre fichier appsettings.json existant a été conservé.",
            "ru" => "Ваш текущий appsettings.json сохранён.",
            _ => "Kept your existing appsettings.json."
        };

    public static string NewOptionsDefaultNotice(string names) =>
        Language switch
        {
            "es" => $"Las nuevas opciones que no están en tu archivo usarán los valores por defecto integrados: {names}.",
            "de" => $"Neue Optionen, die nicht in Ihrer Datei enthalten sind, verwenden die integrierten Standardwerte: {names}.",
            "fr" => $"Les nouvelles options absentes de votre fichier utiliseront les valeurs par défaut intégrées : {names}.",
            "ru" => $"Новые параметры, отсутствующие в вашем файле, используют встроенные значения по умолчанию: {names}.",
            _ => $"New options not in your file use the built-in defaults: {names}."
        };

    // --- Remote Dashboard & Remote PIN Setup Dialog ---

    public static string RemoteDialogTitle =>
        Language switch
        {
            "es" => "Monitor & Control Setup - acceso remoto",
            "de" => "Monitor & Control Setup – Remote-Zugriff",
            "fr" => "Monitor & Control Setup - accès distant",
            "ru" => "Monitor & Control Setup — удалённый доступ",
            _ => "Monitor & Control Setup - remote access"
        };

    public static string RemoteDialogIntro =>
        Language switch
        {
            "es" => "Configure si otros dispositivos de su red (teléfonos, tabletas u otros PC) pueden acceder al panel y realizar cambios.",
            "de" => "Legen Sie fest, ob andere Geräte in Ihrem Netzwerk (Smartphones, Tablets oder andere PCs) auf das Dashboard zugreifen und Änderungen vornehmen können.",
            "fr" => "Configurez si d'autres appareils de votre réseau (smartphones, tablettes ou autres PC) peuvent accéder au tableau de bord et effectuer des modifications.",
            "ru" => "Укажите, смогут ли другие устройства в вашей сети (телефоны, планшеты или другие ПК) открывать панель и вносить изменения.",
            _ => "Configure whether other devices on your network (phones, tablets, or other PCs) can access the dashboard and make changes."
        };

    public static string RemoteEnableOption =>
        Language switch
        {
            "es" => "Activar panel remoto (accesible desde otros dispositivos en su red doméstica)",
            "de" => "Remote-Dashboard aktivieren (erreichbar von anderen Geräten im Heimnetzwerk)",
            "fr" => "Activer le tableau de bord à distance (accessible depuis d'autres appareils du réseau)",
            "ru" => "Включить удалённую панель (доступ с других устройств в домашней сети)",
            _ => "Enable remote dashboard (accessible from other devices on your home network)"
        };

    public static string RemoteDisableOption =>
        Language switch
        {
            "es" => "Solo local (accesible únicamente en este PC en localhost)",
            "de" => "Nur lokal (nur direkt auf diesem PC unter localhost erreichbar)",
            "fr" => "Local uniquement (accessible uniquement sur ce PC via localhost)",
            "ru" => "Только локально (доступ только непосредственно на этом ПК через localhost)",
            _ => "Local only (accessible only directly on this PC at localhost)"
        };

    public static string RemotePortLabel =>
        Language switch
        {
            "es" => "Puerto del panel (predeterminado 5000):",
            "de" => "Dashboard-Port (Standard 5000):",
            "fr" => "Port du tableau de bord (par défaut 5000) :",
            "ru" => "Порт панели (по умолчанию 5000):",
            _ => "Dashboard Port (default 5000):"
        };

    public static string RemotePortInUse =>
        Language switch
        {
            "es" => "El puerto {0} está siendo utilizado por otra aplicación. Elija otro puerto.",
            "de" => "Port {0} wird derzeit von einer anderen Anwendung verwendet. Bitte wählen Sie einen anderen Port.",
            "fr" => "Le port {0} est actuellement utilisé par une autre application. Veuillez choisir un autre port.",
            "ru" => "Порт {0} уже занят другим приложением. Пожалуйста, выберите другой порт.",
            _ => "Port {0} is currently in use by another application. Please choose another port."
        };

    public static string RemotePortInvalid =>
        Language switch
        {
            "es" => "El puerto debe ser un número entre 1024 y 65535.",
            "de" => "Der Port muss eine Zahl zwischen 1024 und 65535 sein.",
            "fr" => "Le port doit être un nombre compris entre 1024 et 65535.",
            "ru" => "Порт должен быть числом от 1024 до 65535.",
            _ => "Port must be a number between 1024 and 65535."
        };

    public static string RemotePinSectionHeader =>
        Language switch
        {
            "es" => "PIN de cambios remotos (necesario para aprobar cambios de tiempo remotamente):",
            "de" => "PIN für Remote-Änderungen (erforderlich zur Genehmigung von Bildschirmzeit aus der Ferne):",
            "fr" => "Code PIN pour modifications à distance (requis pour approuver le temps d'écran à distance) :",
            "ru" => "PIN-код удалённых изменений (нужен для подтверждения экранного времени удалённо):",
            _ => "Remote Changes PIN (required to approve Screen Time changes remotely):"
        };

    public static string RemotePinExistingKeep =>
        Language switch
        {
            "es" => "Mantener el PIN remoto existente",
            "de" => "Bestehende Remote-PIN beibehalten",
            "fr" => "Conserver le code PIN distant existant",
            "ru" => "Сохранить существующий удалённый PIN-код",
            _ => "Keep existing Remote PIN"
        };

    public static string RemotePinExistingChange =>
        Language switch
        {
            "es" => "Definir un nuevo PIN remoto",
            "de" => "Neue Remote-PIN festlegen",
            "fr" => "Définir un nouveau code PIN distant",
            "ru" => "Установить новый удалённый PIN-код",
            _ => "Set a new Remote PIN"
        };

    public static string RemotePinLabel =>
        Language switch
        {
            "es" => "PIN remoto (6-64 caracteres, al menos 3 caracteres distintos):",
            "de" => "Remote-PIN (6-64 Zeichen, mindestens 3 verschiedene Zeichen):",
            "fr" => "Code PIN distant (6-64 caractères, au moins 3 caractères distincts) :",
            "ru" => "Удалённый PIN-код (6-64 символа, не менее 3 различных символов):",
            _ => "Remote PIN (6-64 characters, at least 3 distinct characters):"
        };

    public static string RemotePinConfirmLabel =>
        Language switch
        {
            "es" => "Confirmar PIN:",
            "de" => "PIN bestätigen:",
            "fr" => "Confirmer le code PIN :",
            "ru" => "Подтвердите PIN-код:",
            _ => "Confirm PIN:"
        };

    public static string RemotePinMismatch =>
        Language switch
        {
            "es" => "Los PINs no coinciden.",
            "de" => "Die PINs stimmen nicht überein.",
            "fr" => "Les codes PIN ne correspondent pas.",
            "ru" => "PIN-коды не совпадают.",
            _ => "The PINs do not match."
        };

    public static string RemotePinTooShortOrWeak =>
        Language switch
        {
            "es" => "El PIN debe tener 6-64 caracteres con al menos 3 caracteres distintos.",
            "de" => "Die PIN muss 6-64 Zeichen lang sein und mindestens 3 verschiedene Zeichen enthalten.",
            "fr" => "Le code PIN doit comporter 6 à 64 caractères avec au moins 3 caractères différents.",
            "ru" => "PIN-код должен быть от 6 до 64 символов и содержать не менее 3 различных символов.",
            _ => "PIN must be 6-64 characters with at least 3 different characters."
        };

    public static string RemoteConfigLogEnabled(int port) =>
        Language switch
        {
            "es" => $"Panel remoto: Activado en el puerto {port} (Regla de firewall entrante configurada).",
            "de" => $"Remote-Dashboard: Aktiviert auf Port {port} (Eingehende Firewall-Regel konfiguriert).",
            "fr" => $"Tableau de bord à distance : Activé sur le port {port} (Règle de pare-feu entrant configurée).",
            "ru" => $"Удалённая панель: Включена на порту {port} (Настроено входящее правило брандмауэра).",
            _ => $"Remote dashboard: Enabled on port {port} (Inbound firewall rule configured)."
        };

    public static string RemoteConfigLogDisabled =>
        Language switch
        {
            "es" => "Panel remoto: Desactivado (Solo acceso local en localhost).",
            "de" => "Remote-Dashboard: Deaktiviert (Nur lokaler Zugriff auf localhost).",
            "fr" => "Tableau de bord à distance : Désactivé (Accès local sur localhost uniquement).",
            "ru" => "Удалённая панель: Выключена (Только локальный доступ на localhost).",
            _ => "Remote dashboard: Disabled (Local access on localhost only)."
        };

    public static string RemotePinLogSet =>
        Language switch
        {
            "es" => "PIN de cambios remotos configurado.",
            "de" => "PIN für Remote-Änderungen konfiguriert.",
            "fr" => "Code PIN pour modifications à distance configuré.",
            "ru" => "PIN-код удалённых изменений настроен.",
            _ => "Remote changes PIN configured."
        };

    public static string RemotePinLogKept =>
        Language switch
        {
            "es" => "PIN remoto existente conservado.",
            "de" => "Bestehende Remote-PIN beibehalten.",
            "fr" => "Code PIN distant existant conservé.",
            "ru" => "Существующий удалённый PIN-код сохранён.",
            _ => "Existing Remote PIN preserved."
        };

    public static string RemotePinLogNotSet =>
        Language switch
        {
            "es" => "PIN remoto no configurado (se puede configurar más tarde con GameHost.exe --set-remote-pin).",
            "de" => "Remote-PIN nicht konfiguriert (kann später mit GameHost.exe --set-remote-pin eingerichtet werden).",
            "fr" => "Code PIN distant non configuré (peut être configuré plus tard avec GameHost.exe --set-remote-pin).",
            "ru" => "Удалённый PIN-код не настроен (можно настроить позже с помощью GameHost.exe --set-remote-pin).",
            _ => "Remote PIN not configured (can be configured later with GameHost.exe --set-remote-pin)."
        };
}