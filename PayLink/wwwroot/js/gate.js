/*
 * Test before continue. A form with data-gate="tested" (or "untested") keeps its [data-needs-test] buttons closed until the details
 * passed a test, and closes them again as soon as anything is changed. Choosing an option marked data-no-test (nothing to test,
 * such as SQLite) opens them. The server checks again on every save; this only keeps people from saving untested details.
 */
(function () {
  "use strict";
  document.querySelectorAll("form[data-gate]").forEach(function (form) {
    var tested = form.getAttribute("data-gate") === "tested";
    var buttons = form.querySelectorAll("[data-needs-test]");
    var hint = form.querySelector("[data-gate-hint]");
    function free() {
      return Array.prototype.some.call(form.querySelectorAll("input[type=radio][data-no-test]"), function (r) { return r.checked; });
    }
    function update(changed) {
      var open = tested || free();
      buttons.forEach(function (b) { b.disabled = !open; });
      if (hint) {
        hint.hidden = open;
        hint.textContent = changed ? "Changed since the test: test again to continue." : "Test first. Continue opens when the test passes.";
      }
    }
    function onChange(e) {
      if (!e.target.matches("input, select, textarea") || e.target.type === "hidden") return;
      if (!e.target.hasAttribute("data-no-test")) tested = false;
      update(true);
    }
    form.addEventListener("input", onChange);
    form.addEventListener("change", onChange);
    update(false);
  });
})();
