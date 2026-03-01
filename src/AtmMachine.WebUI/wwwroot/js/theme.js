(function () {
    var root = document.documentElement;
    var toggle = document.getElementById("theme-toggle");
    var storageKey = "consoleatm.theme";

    function currentTheme() {
        var theme = root.getAttribute("data-theme");
        return theme === "dark" ? "dark" : "light";
    }

    function persistTheme(theme) {
        root.setAttribute("data-theme", theme);
        try {
            localStorage.setItem(storageKey, theme);
        } catch (error) {
            // Ignore storage failures (private mode / strict policies).
        }
    }

    function syncButton(theme) {
        if (!toggle) {
            return;
        }

        var isDark = theme === "dark";
        toggle.setAttribute("aria-pressed", String(isDark));
        toggle.textContent = isDark ? "Light mode" : "Dark mode";
        toggle.title = isDark ? "Switch to light mode" : "Switch to dark mode";
    }

    var initial = currentTheme();
    syncButton(initial);

    if (!toggle) {
        return;
    }

    toggle.addEventListener("click", function () {
        var next = currentTheme() === "dark" ? "light" : "dark";
        persistTheme(next);
        syncButton(next);
    });
}());
