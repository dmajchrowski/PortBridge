// REST helpers. Every mutating call carries X-PortBridge (CSRF guard enforced by the server).

export class ApiError extends Error {
  constructor(message, status, errors) {
    super(message);
    this.status = status;
    this.errors = errors || [];
  }
}

export async function api(method, path, body, { raw = false } = {}) {
  const init = { method, headers: { "X-PortBridge": "1" }, credentials: "same-origin" };
  if (body !== undefined) {
    if (raw) init.body = body;
    else {
      init.headers["Content-Type"] = "application/json";
      init.body = JSON.stringify(body);
    }
  }
  let res;
  try {
    res = await fetch(path, init);
  } catch {
    throw new ApiError("PortBridge is not reachable. Is the service running?", 0);
  }
  if (res.status === 401 && !path.startsWith("/api/auth/")) {
    window.dispatchEvent(new CustomEvent("pb:unauthorized"));
    throw new ApiError("Sign in required", 401);
  }
  const type = res.headers.get("content-type") || "";
  const data = type.includes("json") ? await res.json() : await res.text();
  if (!res.ok) throw new ApiError((data && data.error) || `${res.status} ${res.statusText}`, res.status, data && data.errors);
  return data;
}

export const get = (p) => api("GET", p);
export const post = (p, b) => api("POST", p, b ?? {});
export const put = (p, b) => api("PUT", p, b);
export const del = (p) => api("DELETE", p);
