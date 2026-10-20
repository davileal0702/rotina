import { buildRules, shouldRedirect, BLOCK_RULE_ID, FIRST_ALLOW_ID } from "./rules.js";

const STATE_URL = "http://127.0.0.1:47800/api/v1/state";
const BLOCK_PAGE = "/blocked.html";

/** Reads the activity from the local Rotina service and applies (or clears) the site rules. */
async function refresh() {
  let state;
  try {
    const res = await fetch(STATE_URL, { cache: "no-store" });
    if (!res.ok) return;
    state = await res.json();
  } catch {
    return; // service unreachable: keep the rules we have; the service is the only source of truth
  }
  const active = state.state !== "free" && state.allowed && state.session;
  const sites = active ? state.allowed.web : [];
  await chrome.declarativeNetRequest.updateDynamicRules({
    removeRuleIds: [BLOCK_RULE_ID, FIRST_ALLOW_ID],
    addRules: active ? buildRules(sites, BLOCK_PAGE) : [],
  });

  // When a new activity starts, tabs that were already open on other sites go to the block page too.
  const sessionId = active ? state.session.id : null;
  const { lastSessionId = null } = await chrome.storage.session.get("lastSessionId");
  if (sessionId && sessionId !== lastSessionId) {
    for (const tab of await chrome.tabs.query({})) {
      if (tab.id !== undefined && tab.url && shouldRedirect(tab.url, sites))
        await chrome.tabs.update(tab.id, { url: chrome.runtime.getURL(BLOCK_PAGE) });
    }
  }
  if (sessionId !== lastSessionId) await chrome.storage.session.set({ lastSessionId: sessionId });
}

// MV3 workers sleep when idle: the alarm (30 s is Chrome's minimum) wakes it, navigation refreshes
// right away, and while awake it polls every few seconds.
chrome.runtime.onInstalled.addListener(refresh);
chrome.runtime.onStartup.addListener(refresh);
chrome.alarms.create("refresh", { periodInMinutes: 0.5 });
chrome.alarms.onAlarm.addListener(refresh);
chrome.webNavigation.onBeforeNavigate.addListener((d) => { if (d.frameId === 0) refresh(); });
setInterval(refresh, 3000);
refresh();
