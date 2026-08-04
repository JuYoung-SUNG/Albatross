// 4가지 배경 테마(White/Blue/Yellow/Green) 전환 + localStorage 저장
window.albatrossTheme = (function () {
    const KEY = 'albatross-theme';
    const THEMES = ['white', 'blue', 'yellow', 'green'];

    function apply(theme) {
        document.documentElement.setAttribute('data-theme', theme);
    }

    function get() {
        const saved = localStorage.getItem(KEY);
        return THEMES.includes(saved) ? saved : 'white';
    }

    function set(theme) {
        if (!THEMES.includes(theme)) return;
        localStorage.setItem(KEY, theme);
        apply(theme);
    }

    apply(get());

    return { get, set };
})();
