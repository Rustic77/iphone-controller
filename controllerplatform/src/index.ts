import { loadConfig } from "./config.js";
import { buildServer } from "./server.js";
import { DevUserStore } from "./stores/userStore.js";
import { loadDeviceStoreFromFile } from "./stores/deviceStore.js";

async function main(): Promise<void> {
  const config = loadConfig();

  const userStore = new DevUserStore(config.devUsername, config.devPassword, config.devUserId);
  const deviceStore = loadDeviceStoreFromFile(config.devicesFile);

  const app = await buildServer({ config, userStore, deviceStore });

  try {
    await app.listen({ host: config.host, port: config.port });
    app.log.info(
      { host: config.host, port: config.port },
      `control relay listening — UI at http://localhost:${config.port}/`,
    );
  } catch (err) {
    app.log.error({ err }, "failed to start");
    process.exit(1);
  }

  for (const sig of ["SIGINT", "SIGTERM"] as const) {
    process.on(sig, () => {
      app.log.info({ sig }, "shutting down");
      app.close().then(() => process.exit(0));
    });
  }
}

main().catch((err) => {
  // Config/startup errors before the logger exists.
  console.error(err);
  process.exit(1);
});
