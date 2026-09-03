// Menus suspensos da barra de menus (details/summary): fecha ao clicar fora,
// ao escolher um item ou ao navegar.
(function () {
    function closeAll(except) {
        document.querySelectorAll(".menubar-nav details[open]").forEach(function (d) {
            if (d !== except) d.removeAttribute("open");
        });
    }
    document.addEventListener("click", function (e) {
        var details = e.target.closest(".menubar-nav details");
        if (!details) { closeAll(); return; }
        if (e.target.closest("a")) { closeAll(); return; }
        closeAll(details);
    });
    document.addEventListener("keydown", function (e) { if (e.key === "Escape") closeAll(); });
    window.addEventListener("popstate", function () { closeAll(); });
})();
