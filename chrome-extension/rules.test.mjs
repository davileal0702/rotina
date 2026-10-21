// Run: node --test chrome-extension/rules.test.mjs
import { test } from "node:test";
import assert from "node:assert/strict";
import { isAllowedHost, shouldRedirect, buildRules, serviceGoneTooLong } from "./rules.js";

test("rules stay while the service is briefly away, and go after 2 hours", () => {
  const now = Date.parse("2026-10-21T12:00:00Z");
  assert.equal(serviceGoneTooLong(now - 5 * 60 * 1000, now), false);
  assert.equal(serviceGoneTooLong(now - 2 * 60 * 60 * 1000, now), false);
  assert.equal(serviceGoneTooLong(now - 2 * 60 * 60 * 1000 - 1, now), true);
});

const allowed = ["youtube.com", "github.com"];

test("allowed domain includes subdomains but not look-alikes", () => {
  assert.equal(isAllowedHost("youtube.com", allowed), true);
  assert.equal(isAllowedHost("m.YouTube.com", allowed), true);
  assert.equal(isAllowedHost("notyoutube.com", allowed), false);
  assert.equal(isAllowedHost("youtube.com.evil.net", allowed), false);
});

test("only http(s) pages outside the list are redirected", () => {
  assert.equal(shouldRedirect("https://www.instagram.com/", allowed), true);
  assert.equal(shouldRedirect("https://github.com/x/y", allowed), false);
  assert.equal(shouldRedirect("chrome://extensions", allowed), false);
  assert.equal(shouldRedirect("chrome-extension://abc/blocked.html", allowed), false);
  assert.equal(shouldRedirect("not a url", allowed), false);
});

test("rules: block everything, allow listed domains with higher priority", () => {
  const rules = buildRules(allowed, "chrome-extension://id/blocked.html");
  assert.equal(rules.length, 2);
  // The blocked address rides along after "#", so the page can go back to it later.
  assert.equal(rules[0].action.redirect.regexSubstitution, "chrome-extension://id/blocked.html#\\0");
  assert.ok(rules[1].priority > rules[0].priority);
  assert.deepEqual(rules[1].condition.requestDomains, allowed);
  assert.equal(buildRules([], "/blocked.html").length, 1);
});
