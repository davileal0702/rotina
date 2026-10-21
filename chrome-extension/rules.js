// Pure helpers shared by the service worker and the tests.

export const BLOCK_RULE_ID = 1;
export const FIRST_ALLOW_ID = 2;
export const SERVICE_GRACE_MS = 2 * 60 * 60 * 1000;

/** The service has not answered for 2 hours: give up blocking rather than block forever. */
export function serviceGoneTooLong(lastOkMs, nowMs) {
  return nowMs - lastOkMs > SERVICE_GRACE_MS;
}

/** "youtube.com" allows youtube.com and any subdomain (m.youtube.com), like the SPEC says. */
export function isAllowedHost(host, allowedSites) {
  host = host.toLowerCase().replace(/\.$/, "");
  return allowedSites.some((d) => host === d || host.endsWith("." + d));
}

/** Only real web pages are checked; chrome://, the extension's own pages, file:// etc. are left alone. */
export function shouldRedirect(url, allowedSites) {
  let u;
  try { u = new URL(url); } catch { return false; }
  if (u.protocol !== "http:" && u.protocol !== "https:") return false;
  return !isAllowedHost(u.hostname, allowedSites);
}

/**
 * declarativeNetRequest rules for an activity: redirect every page and frame to the block page,
 * except the allowed domains (higher priority). requestDomains already matches subdomains.
 */
export function buildRules(allowedSites, blockPagePath) {
  const types = ["main_frame", "sub_frame"]; // images/scripts from other domains still load, so allowed sites work
  const rules = [{
    id: BLOCK_RULE_ID,
    priority: 1,
    action: { type: "redirect", redirect: { extensionPath: blockPagePath } },
    condition: { regexFilter: "^https?://", resourceTypes: types },
  }];
  if (allowedSites.length > 0) {
    rules.push({
      id: FIRST_ALLOW_ID,
      priority: 2,
      action: { type: "allow" },
      condition: { requestDomains: allowedSites, resourceTypes: types },
    });
  }
  return rules;
}
