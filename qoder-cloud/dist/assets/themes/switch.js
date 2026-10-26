(() => {
  "use strict";
  const root = document.documentElement;
  const current = root.dataset.theme || "paper";
  const here = location.pathname.split("/").pop() || "index.html";
  const themes = [
    { id: "paper", href: "index.html", name: "纸张", sub: "安静的工作台", s1: "#f5f0e6", s2: "#b05a2b" },
    { id: "ow", href: "theme-overwatch.html", name: "守望先锋", sub: "行动简报", s1: "#071018", s2: "#f99e1a" },
    { id: "time", href: "theme-time.html", name: "神秘博士", sub: "时间旅行", s1: "#071126", s2: "#e9b85c" },
    { id: "magic", href: "theme-magic.html", name: "哈利波特", sub: "羊皮纸", s1: "#f0e4c8", s2: "#9a6b2f" },
  ];

  const dock = document.createElement("div");
  dock.className = "theme-dock";
  dock.id = "themeDock";
  dock.innerHTML =
    '<button class="theme-btn" type="button" aria-expanded="false" aria-controls="themePanel" aria-label="切换官网主题">' +
      '<span class="theme-glyph" aria-hidden="true"><i></i><i></i><i></i><i></i></span>主题' +
    '</button>' +
    '<div class="theme-panel" id="themePanel" hidden>' +
      '<h2>换一种看法</h2>' +
      themes.map((t) => {
        const active = t.id === current || t.href === here;
        return '<a class="theme-opt" href="' + t.href + '" data-theme-id="' + t.id + '"' + (active ? ' aria-current="page"' : '') + '>' +
          '<span class="sw" style="--s1:' + t.s1 + ';--s2:' + t.s2 + '" aria-hidden="true"></span>' +
          '<span class="tx"><b>' + t.name + '</b><small>' + t.sub + '</small></span>' +
          (active ? '<em>当前</em>' : '') +
        '</a>';
      }).join("") +
      '<p class="theme-note">四套外观，同一款工具。致敬主题均为非官方同人设计，与版权方无关。</p>' +
    '</div>';
  document.body.appendChild(dock);

  const btn = dock.querySelector(".theme-btn");
  const panel = dock.querySelector(".theme-panel");
  const links = Array.from(panel.querySelectorAll(".theme-opt"));
  let closeTimer = 0;

  function open() {
    clearTimeout(closeTimer);
    panel.hidden = false;
    dock.classList.add("open");
    btn.setAttribute("aria-expanded", "true");
    panel.classList.remove("opening");
    void panel.offsetWidth;
    panel.classList.add("opening");
    if (links.length) links[0].focus({ preventScroll: true });
  }

  function close(restore) {
    dock.classList.remove("open");
    btn.setAttribute("aria-expanded", "false");
    panel.classList.remove("opening");
    closeTimer = setTimeout(() => { panel.hidden = true; }, 120);
    if (restore) btn.focus({ preventScroll: true });
  }

  btn.addEventListener("click", () => {
    if (panel.hidden) open();
    else close(false);
  });

  document.addEventListener("pointerdown", (e) => {
    if (!dock.contains(e.target) && !panel.hidden) close(false);
  });

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && !panel.hidden) {
      e.preventDefault();
      close(true);
    }
    if (panel.hidden) return;
    if (e.key === "ArrowDown" || e.key === "ArrowUp") {
      e.preventDefault();
      const i = links.indexOf(document.activeElement);
      const next = e.key === "ArrowDown" ? (i + 1 + links.length) % links.length : (i - 1 + links.length) % links.length;
      links[next].focus({ preventScroll: true });
    }
  });

  links.forEach((a) => {
    a.addEventListener("click", (e) => {
      if (a.getAttribute("aria-current") === "page") {
        e.preventDefault();
        close(true);
        return;
      }
      const leave = window.__themeLeave;
      if (typeof leave === "function" && !matchMedia("(prefers-reduced-motion: reduce)").matches) {
        e.preventDefault();
        close(false);
        leave(a.getAttribute("href"));
      }
    });
  });
})();
