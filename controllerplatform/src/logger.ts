/**
 * Minimal structured-logger interface. It is intentionally pino-compatible
 * (obj-first, then message) so the real Fastify/pino logger can be passed
 * straight into the Hub, while tests can inject a silent stub.
 */
export interface Logger {
  debug(obj: unknown, msg?: string): void;
  info(obj: unknown, msg?: string): void;
  warn(obj: unknown, msg?: string): void;
  error(obj: unknown, msg?: string): void;
}

/** A logger that discards everything — used in unit tests. */
export const silentLogger: Logger = {
  debug() {},
  info() {},
  warn() {},
  error() {},
};
