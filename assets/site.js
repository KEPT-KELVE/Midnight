// midnightOS website: mobile menu, the tabbed showcase, and FAQ expand/collapse.
(function () {
  "use strict";

  var toggle = document.querySelector(".nav-toggle");
  var nav = document.querySelector(".site-nav");
  if (toggle && nav) {
    toggle.addEventListener("click", function () {
      var open = nav.classList.toggle("open");
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
    });
  }

  document.querySelectorAll("[data-showcase]").forEach(function (root) {
    var slides = root.querySelectorAll(".slide");
    var tabs = root.querySelectorAll(".showcase-tabs button");
    var count = root.querySelector(".count");
    var index = 0;
    function show(i) {
      index = (i + slides.length) % slides.length;
      slides.forEach(function (s, k) { s.hidden = k !== index; });
      tabs.forEach(function (t, k) { t.setAttribute("aria-selected", k === index ? "true" : "false"); });
      if (count) count.textContent = (index + 1) + " of " + slides.length;
    }
    root.querySelector(".prev").addEventListener("click", function () { show(index - 1); });
    root.querySelector(".next").addEventListener("click", function () { show(index + 1); });
    tabs.forEach(function (t, k) { t.addEventListener("click", function () { show(k); }); });
    show(0);
  });

  document.querySelectorAll("[data-faq]").forEach(function (root) {
    var all = root.querySelectorAll("details");
    var expand = root.querySelector("[data-expand]");
    var collapse = root.querySelector("[data-collapse]");
    if (expand) expand.addEventListener("click", function () { all.forEach(function (d) { d.open = true; }); });
    if (collapse) collapse.addEventListener("click", function () { all.forEach(function (d) { d.open = false; }); });
  });
})();
