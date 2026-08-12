import "dotenv/config";

/** Strongly-typed, validated view of process environment. */
export interface Config {
  host: string;
  port: number;
  serverSecret: string;
  devUsername: string;
  devPassword: string;
  devUserId: string;
  devicesFile: string;
  heartbeatIntervalMs: number;
  heartbeatTimeoutMs: number;
  staleCommandMs: number;
  sessionTtlMs: number;
  logLevel: string;
}

function required(name: string): string {
  const v = process.env[name];
  if (!v || v.trim() === "") {
    throw new Error(`Missing required environment variable: ${name}`);
  }
  return v;
}

function num(name: string, fallback: number): number {
  const v = process.env[name];
  if (v === undefined || v.trim() === "") return fallback;
  const n = Number(v);
  if (!Number.isFinite(n)) throw new Error(`Environment variable ${name} must be a number`);
  return n;
}

export function loadConfig(): Config {
  const serverSecret = required("SERVER_SECRET");
  if (serverSecret === "change-me-to-a-long-random-string") {
    throw new Error("SERVER_SECRET is still the placeholder value — set a real secret in .env");
  }

  return {
    host: process.env.HOST ?? "0.0.0.0",
    port: num("PORT", 8080),
    serverSecret,
    devUsername: required("DEV_USERNAME"),
    devPassword: required("DEV_PASSWORD"),
    devUserId: process.env.DEV_USER_ID ?? "dev-operator",
    devicesFile: process.env.DEVICES_FILE ?? "./devices.json",
    heartbeatIntervalMs: num("HEARTBEAT_INTERVAL_MS", 10_000),
    heartbeatTimeoutMs: num("HEARTBEAT_TIMEOUT_MS", 30_000),
    staleCommandMs: num("STALE_COMMAND_MS", 2_000),
    sessionTtlMs: num("SESSION_TTL_MS", 12 * 60 * 60 * 1000),
    logLevel: process.env.LOG_LEVEL ?? "info",
  };
}
