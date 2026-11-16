namespace MonitorAndControl.ScreenTime;

public static class ScreenTimeLocalization
{
    public static string BreakStart(int breakMinutes, string time, string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => $"Es hora de un descanso de {breakMinutes} minutos. El PC se bloqueará ahora y podrá usarse de nuevo a las {time}.",
            "de" => $"Zeit für eine {breakMinutes}-minütige Pause. Der PC wird jetzt gesperrt und kann um {time} wieder verwendet werden.",
            "fr" => $"C'est l'heure d'une pause de {breakMinutes} minutes. Le PC se verrouille maintenant et pourra être réutilisé à {time}.",
            "ru" => $"Время для перерыва на {breakMinutes} мин. ПК сейчас заблокируется и будет доступен снова в {time}.",
            _ => $"Time for a {breakMinutes}-minute break. The PC locks now and can be used again at {time}."
        };

    public static string BreakUpcoming(int breakMinutes, int warnMinutes, string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => $"Un descanso de {breakMinutes} minutos comenzará en unos {warnMinutes} minuto(s). Guarda tu trabajo.",
            "de" => $"Eine {breakMinutes}-minütige Pause beginnt in etwa {warnMinutes} Minute(n). Speichere deine Arbeit.",
            "fr" => $"Une pause de {breakMinutes} minutes commence dans environ {warnMinutes} minute(s). Enregistrez votre travail.",
            "ru" => $"Перерыв на {breakMinutes} мин начнётся примерно через {warnMinutes} мин. Сохраните свою работу.",
            _ => $"A {breakMinutes}-minute break starts in about {warnMinutes} minute(s). Save your work."
        };

    public static string SignOutBedtime(string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => "Es hora de dormir. Se cerrará la sesión en unos segundos.",
            "de" => "Es ist Schlafenszeit. Du wirst in wenigen Sekunden abgemeldet.",
            "fr" => "C'est l'heure du coucher. Vous serez déconnecté dans quelques secondes.",
            "ru" => "Время сна. Сеанс будет завершён через несколько секунд.",
            _ => "It is bedtime. You will be signed out in a few seconds."
        };

    public static string SignOutQuota(string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => "No queda tiempo de PC hoy. Se cerrará la sesión en unos segundos.",
            "de" => "Heute ist keine PC-Zeit mehr übrig. Du wirst in wenigen Sekunden abgemeldet.",
            "fr" => "Il ne reste plus de temps de PC aujourd'hui. Vous serez déconnecté dans quelques secondes.",
            "ru" => "На сегодня время за ПК исчерпано. Сеанс будет завершён через несколько секунд.",
            _ => "There is no PC time left today. You will be signed out in a few seconds."
        };

    public static string BedtimeWarning(int minutes, string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => $"Hora de dormir en unos {minutes} minuto(s). Guarda tu trabajo: se cerrará la sesión.",
            "de" => $"Schlafenszeit in etwa {minutes} Minute(n). Speichere deine Arbeit – du wirst dann abgemeldet.",
            "fr" => $"Heure du coucher dans environ {minutes} minute(s). Enregistrez votre travail - la session sera fermée.",
            "ru" => $"Время сна примерно через {minutes} мин. Сохраните работу — сеанс будет завершён.",
            _ => $"Bedtime in about {minutes} minute(s). Save your work - you will be signed out then."
        };

    public static string QuotaWarning(int minutes, string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => $"Quedan unos {minutes} minuto(s) de tiempo de PC hoy. Guarda tu trabajo: se cerrará la sesión cuando se agote el tiempo.",
            "de" => $"Noch etwa {minutes} Minute(n) PC-Zeit heute übrig. Speichere deine Arbeit – du wirst abgemeldet, wenn die Zeit abläuft.",
            "fr" => $"Environ {minutes} minute(s) de temps de PC restant aujourd'hui. Enregistrez votre travail - la session sera fermée une fois le temps écoulé.",
            "ru" => $"Осталось около {minutes} мин времени за ПК сегодня. Сохраните работу — сеанс будет завершён, когда время истечёт.",
            _ => $"About {minutes} minute(s) of PC time left today. Save your work - you will be signed out when time runs out."
        };

    public static string AskMoreTime(string baseText, string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => $"{baseText} ¿Pedir más tiempo a tus padres?",
            "de" => $"{baseText} Eltern nach mehr Zeit fragen?",
            "fr" => $"{baseText} Demander plus de temps à vos parents ?",
            "ru" => $"{baseText} Попросить у родителей больше времени?",
            _ => $"{baseText} Ask your parent for more time?"
        };

    public static string AskMoreTimeSent(string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => "Se ha enviado la petición a tus padres. Se te avisará si añaden tiempo.",
            "de" => "Deine Eltern wurden gefragt. Du wirst benachrichtigt, wenn sie Zeit hinzufügen.",
            "fr" => "La demande a été envoyée à vos parents. Vous serez averti s'ils ajoutent du temps.",
            "ru" => "Запрос отправлен родителям. Вы получите оповещение, если они добавят время.",
            _ => "Your parent has been asked. You will be told if they add time."
        };

    public static string AskMoreTimeAlreadySent(string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => "Ya se ha enviado la petición a tus padres. Por favor, espera su respuesta.",
            "de" => "Deine Eltern wurden bereits gefragt. Bitte warte auf ihre Antwort.",
            "fr" => "La demande a déjà été envoyée à vos parents. Veuillez attendre leur réponse.",
            "ru" => "Запрос уже был отправлен родителям. Пожалуйста, подождите их ответа.",
            _ => "Your parent has already been asked. Please wait for their answer."
        };

    public static string AskMoreTimeDismissed(string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => "Tus padres no han añadido más tiempo esta vez.",
            "de" => "Deine Eltern haben diesmal keine Zeit hinzugefügt.",
            "fr" => "Vos parents n'ont pas ajouté de temps cette fois-ci.",
            "ru" => "Родители не добавили времени в этот раз.",
            _ => "Your parent did not add more time this time."
        };

    public static string ProgramAllowed(string program, string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => $"Tus padres han permitido {program}. Ya puedes abrirlo.",
            "de" => $"Deine Eltern haben {program} erlaubt. Du kannst es jetzt öffnen.",
            "fr" => $"Vos parents ont autorisé {program}. Vous pouvez l'ouvrir maintenant.",
            "ru" => $"Родители разрешили {program}. Теперь вы можете его открыть.",
            _ => $"Your parent allowed {program}. You can open it now."
        };

    public static string ProgramBlocked(string program, string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => $"Tus padres no han permitido {program}.",
            "de" => $"Deine Eltern haben {program} nicht erlaubt.",
            "fr" => $"Vos parents n'ont pas autorisé {program}.",
            "ru" => $"Родители не разрешили {program}.",
            _ => $"Your parent did not allow {program}."
        };

    public static string HomeworkActive(string time, string? lang) =>
        ScreenTimeFiles.NormalizeLanguage(lang) switch
        {
            "es" => $"El modo deberes está activado hasta las {time}. Los juegos y el entretenimiento están bloqueados hasta entonces.",
            "de" => $"Der Hausaufgabenmodus ist bis {time} aktiv. Spiele und Unterhaltung sind bis dahin blockiert.",
            "fr" => $"Le mode devoirs est activé jusqu'à {time}. Les jeux et divertissements sont bloqués jusqu'à cette heure.",
            "ru" => $"Режим уроков включён до {time}. Игры и развлечения заблокированы до этого времени.",
            _ => $"Homework mode is on until {time}. Games and entertainment are blocked until then."
        };
}
