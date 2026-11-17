// midnightOS website: star field, screen tour, release details, lightbox, reveal-on-scroll.
(function () {
  "use strict";

  var reduceMotion = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  // ---------- night sky ----------
  var canvas = document.getElementById("sky");
  var ctx = canvas.getContext("2d");
  var stars = [];
  function resize() {
    var dpr = Math.min(window.devicePixelRatio || 1, 2);
    canvas.width = innerWidth * dpr;
    canvas.height = innerHeight * dpr;
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    var count = Math.round(innerWidth * innerHeight / 9000);
    stars = [];
    for (var i = 0; i < count; i++) {
      stars.push({ x: Math.random() * innerWidth, y: Math.random() * innerHeight,
                   r: Math.random() * 1.1 + 0.3, p: Math.random() * Math.PI * 2, s: 0.4 + Math.random() * 0.8 });
    }
  }
  function draw(t) {
    var w = innerWidth, h = innerHeight;
    ctx.clearRect(0, 0, w, h);
    var g = ctx.createRadialGradient(w * 0.2, h * 1.05, 0, w * 0.2, h * 1.05, Math.max(w, h) * 0.8);
    g.addColorStop(0, "rgba(106,0,255,0.20)");
    g.addColorStop(1, "rgba(106,0,255,0)");
    ctx.fillStyle = g;
    ctx.fillRect(0, 0, w, h);
    var g2 = ctx.createRadialGradient(w * 0.9, -h * 0.05, 0, w * 0.9, -h * 0.05, Math.max(w, h) * 0.6);
    g2.addColorStop(0, "rgba(60,40,200,0.16)");
    g2.addColorStop(1, "rgba(60,40,200,0)");
    ctx.fillStyle = g2;
    ctx.fillRect(0, 0, w, h);
    for (var i = 0; i < stars.length; i++) {
      var s = stars[i];
      var a = 0.25 + 0.45 * (0.5 + 0.5 * Math.sin(t / 1000 * s.s + s.p));
      ctx.globalAlpha = a;
      ctx.fillStyle = "#fff";
      ctx.beginPath();
      ctx.arc(s.x, s.y, s.r, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
    if (!reduceMotion) requestAnimationFrame(draw);
  }
  resize();
  addEventListener("resize", resize);
  requestAnimationFrame(draw);

  // ---------- screen tour ----------
  var screens = [
    ["start", "Start", "Your apps and folders", "Start opens in the middle of the screen with your pinned apps, folders and a search box that finds apps, files and settings."],
    ["desktop", "Desktop", "Clean and quiet", "A focused desktop with a floating taskbar, live window previews and the Developer Preview watermark."],
    ["settings", "Settings", "Everything in one place", "Settings is organised by what you want to do, with recommended settings on the home page."],
    ["metro", "Metro Browser", "The web, in motion", "Metro Browser greets you with an animated tile wall, then lets you pick your colour and favourites."],
    ["metro-welcome", "Make it yours", "Colours and favourites", "Pick a colour for tiles and highlights. Every icon in the browser is drawn for midnightOS."],
    ["setup", "Install midnightOS", "From USB to disk", "Pick a disk, confirm, and Setup copies midnightOS to your PC, with a clear progress bar."],
    ["oobe", "First start", "Wi-Fi, name, PIN, face", "After installing, a short full-screen setup gets your time zone, network, account, PIN and face sign-in ready."],
    ["lock", "Sign in", "PIN or face", "Sign in with your PIN, or just look at the screen if you set up face sign-in."]
  ];
  var tabs = document.querySelector(".tour-tabs");
  var image = document.getElementById("tourImage");
  var caption = document.getElementById("tourCaption");
  var current = 0, timer = null;
  screens.forEach(function (screen, i) {
    var b = document.createElement("button");
    b.className = "tour-tab";
    b.setAttribute("role", "tab");
    b.innerHTML = "<strong></strong><span></span>";
    b.querySelector("strong").textContent = screen[1];
    b.querySelector("span").textContent = screen[2];
    b.addEventListener("click", function () { show(i); stopAuto(); });
    tabs.appendChild(b);
    var preload = new Image();
    preload.src = "assets/shots/" + screen[0] + ".jpg";
  });
  function show(i) {
    current = i;
    var screen = screens[i];
    Array.prototype.forEach.call(tabs.children, function (b, k) { b.setAttribute("aria-selected", k === i ? "true" : "false"); });
    image.classList.add("fading");
    setTimeout(function () {
      image.src = "assets/shots/" + screen[0] + ".jpg";
      image.alt = screen[1] + ": " + screen[3];
      caption.textContent = screen[3];
      image.classList.remove("fading");
    }, reduceMotion ? 0 : 180);
  }
  function stopAuto() { if (timer) { clearInterval(timer); timer = null; } }
  show(0);
  if (!reduceMotion) timer = setInterval(function () { show((current + 1) % screens.length); }, 5500);

  // ---------- lightbox ----------
  var box = document.getElementById("lightbox");
  var boxImg = box.querySelector("img");
  function open(src, alt) { boxImg.src = src; boxImg.alt = alt || ""; box.hidden = false; stopAuto(); }
  function close() { box.hidden = true; }
  document.querySelector(".tour-zoom").addEventListener("click", function () { open(image.src, image.alt); });
  document.querySelector(".hero-shot img").addEventListener("click", function (e) { open(e.target.src, e.target.alt); });
  box.addEventListener("click", close);
  addEventListener("keydown", function (e) { if (e.key === "Escape") close(); });

  // ---------- release details (release.json, written by tools/split-release.py) ----------
  function size(bytes) {
    var gb = bytes / 1073741824;
    return gb >= 1 ? gb.toFixed(2) + " GB" : (bytes / 1048576).toFixed(0) + " MB";
  }
  fetch("release.json", { cache: "no-cache" }).then(function (r) {
    if (!r.ok) throw new Error(r.status);
    return r.json();
  }).then(function (rel) {
    if (!rel || !rel.parts || !rel.parts.length) throw new Error("empty");
    document.querySelectorAll('[data-release="version"]').forEach(function (el) { el.textContent = rel.version; });
    document.querySelectorAll('[data-release="size"]').forEach(function (el) { el.textContent = size(rel.size); });
    document.getElementById("isoMeta").textContent = rel.name + " · " + size(rel.size);
    var list = document.getElementById("isoParts");
    list.innerHTML = "";
    var names = [];
    rel.parts.forEach(function (part, i) {
      var name = part.url.split("/").pop();
      names.push(name);
      var li = document.createElement("li");
      var a = document.createElement("a");
      a.href = part.url;
      a.innerHTML = "<strong></strong><span></span>";
      a.querySelector("strong").textContent = "Part " + (i + 1) + " of " + rel.parts.length;
      a.querySelector("span").textContent = size(part.size);
      li.appendChild(a);
      list.appendChild(li);
    });
    document.getElementById("joinWin").textContent = "copy /b " + names.join(" + ") + " " + rel.name;
    document.getElementById("joinNix").textContent = "cat " + names.join(" ") + " > " + rel.name;
    document.getElementById("isoHash").textContent = rel.sha256;
  }).catch(function () {
    document.getElementById("isoParts").innerHTML = '<li class="muted">The download list isn\'t published yet. Check back soon.</li>';
  });

  // ---------- reveal on scroll ----------
  var targets = document.querySelectorAll(".section-head, .card, .step-list li, .dl-card, .tour-grid, .faq details, .reqs");
  if ("IntersectionObserver" in window && !reduceMotion) {
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (e) { if (e.isIntersecting) { e.target.classList.add("in"); io.unobserve(e.target); } });
    }, { rootMargin: "0px 0px -8% 0px" });
    targets.forEach(function (t) { t.classList.add("reveal"); io.observe(t); });
  }
})();
