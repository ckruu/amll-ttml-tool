/// <reference types="vite/client" />
/// <reference types="vite-plugin-pwa/client" />
declare module "virtual:i18next-loader" {
	const value: typeof import("../../locales/en-US/translation.json");
	export default value;
}
declare module "virtual:buildmeta" {
	export const BUILD_TIME: string;
	export const GIT_COMMIT: string;
}
interface ImportMetaEnv {
	readonly TAURI_ENV_DEBUG?: string;
	readonly TAURI_ENV_TARGET_TRIPLE?: string;
	readonly TAURI_ENV_ARCH?: string;
	readonly TAURI_ENV_PLATFORM?: string;
	readonly TAURI_ENV_FAMILY?: string;
}
interface ImportMeta {
	readonly env: ImportMetaEnv;
}
