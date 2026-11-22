// The browser side of notifications, called from Blazor (Services/PushService.cs).

const worker = 'service-worker.js';

function isIos() {
  return /iPhone|iPad|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
}

function isInstalled() {
  return window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
}

export async function status() {
  const supported = 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
  const result = { supported, ios: isIos(), installed: isInstalled(), permission: supported ? Notification.permission : 'unsupported', subscribed: false };
  if (!supported) return result;
  const reg = await navigator.serviceWorker.getRegistration();
  result.subscribed = !!(reg && await reg.pushManager.getSubscription());
  return result;
}

function keyBytes(base64Url) {
  const padded = (base64Url + '='.repeat((4 - base64Url.length % 4) % 4)).replace(/-/g, '+').replace(/_/g, '/');
  return Uint8Array.from(atob(padded), c => c.charCodeAt(0));
}

/** Asks permission (must follow a tap) and subscribes. Returns the subscription, or null if the person said no. */
export async function enable(publicKey) {
  const permission = await Notification.requestPermission();
  if (permission !== 'granted') return null;
  const reg = await navigator.serviceWorker.register(worker);
  await navigator.serviceWorker.ready;
  const sub = (await reg.pushManager.getSubscription())
    ?? await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: keyBytes(publicKey) });
  const json = sub.toJSON();
  return { endpoint: json.endpoint, keys: { p256dh: json.keys.p256dh, auth: json.keys.auth } };
}

/** The current subscription, to re-send to the server after signing in on a browser that already said yes. */
export async function current() {
  const reg = await navigator.serviceWorker?.getRegistration();
  const sub = reg && await reg.pushManager.getSubscription();
  if (!sub) return null;
  const json = sub.toJSON();
  return { endpoint: json.endpoint, keys: { p256dh: json.keys.p256dh, auth: json.keys.auth } };
}

/** Unsubscribes this browser. Returns the old endpoint so the server can forget it too. */
export async function disable() {
  const reg = await navigator.serviceWorker?.getRegistration();
  const sub = reg && await reg.pushManager.getSubscription();
  if (!sub) return null;
  const endpoint = sub.endpoint;
  await sub.unsubscribe();
  return endpoint;
}
