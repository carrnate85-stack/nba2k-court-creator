// Semantic palette shared by the Electron shell and both browser editors.
(function (root) {
  const palette = {
    WindowBrush: ["#ECEDEF", "#0B1119"],
    PanelBrush: ["#F7F7F9", "#101923"],
    PanelRaisedBrush: ["#FDFDFE", "#172230"],
    PanelHoverBrush: ["#E7E9EE", "#1D2B3B"],
    InputBrush: ["#F1F2F5", "#121C28"],
    WorkspaceBrush: ["#E1E3E7", "#0D141D"],
    BorderBrush: ["#DCDEE4", "#2A3A4C"],
    StrongBorderBrush: ["#BFC3CD", "#3B526A"],
    TextBrush: ["#272830", "#F1F6FC"],
    MutedTextBrush: ["#686B76", "#9BAEC4"],
    AccentBrush: ["#6E86AC", "#2593FF"],
    AccentBrightBrush: ["#4D72AA", "#40B5FF"],
    AccentDarkBrush: ["#DDE5F1", "#103F5E"],
    TealBrush: ["#577B88", "#0B7482"],
    ActionBrush: ["#30313A", "#075A67"],
    ActionTextBrush: ["#FFFFFF", "#FFFFFF"],
    MenuPopupBrush: ["#FCFCFD", "#111C28"],
    MenuHoverBrush: ["#E9ECF3", "#203247"],
    MenuOpenBrush: ["#E2E6EE", "#18344E"],
    MenuSeparatorBrush: ["#E2E4E9", "#2A3C50"],
    ScrollTrackBrush: ["#E9EBEF", "#0C151F"],
    ScrollThumbBrush: ["#BCC1CC", "#405369"],
    SliderRailBrush: ["#D5D9E1", "#34475B"],
    SliderThumbBrush: ["#FFFFFF", "#E8F2FF"],
    SafetyBrush: ["#ECF4F0", "#153126"],
    SafetyBorderBrush: ["#C3DACB", "#2A7255"],
    SafetyTextBrush: ["#3A6550", "#A9E7C9"],
    WarningTextBrush: ["#8A5A22", "#FFBE78"],
  };

  function colors(name = "light") {
    const index = name === "dark" ? 1 : 0;
    return Object.fromEntries(Object.entries(palette).map(([key, values]) => [key, values[index]]));
  }

  function apply(name, persist = false) {
    name = name === "dark" ? "dark" : "light";
    const element = root.document.documentElement;
    element.dataset.theme = name;
    element.style.colorScheme = name;
    for (const [key, value] of Object.entries(colors(name))) element.style.setProperty(`--${key}`, value);
    if (persist) {
      try { root.localStorage.setItem("courtCreator.studioTheme", name); } catch {}
    }
    return name;
  }

  const api = { colors, apply };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else {
    root.StudioTheme = api;
    let name = "light";
    try { name = root.localStorage.getItem("courtCreator.studioTheme") || "light"; } catch {}
    const requested = new URLSearchParams(root.location.search).get("theme");
    apply(requested === "dark" || requested === "light" ? requested : name);
  }
})(typeof window === "undefined" ? globalThis : window);
