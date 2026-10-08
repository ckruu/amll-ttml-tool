import { defineConfig } from "i18next-cli";
export default defineConfig({
	locales: ["en-US"],
	extract: {
		input: "src/**/*.{js,jsx,ts,tsx}",
		ignore: ["**/wasm/**", "**/vendor/**"],
		output: "locales\\{{language}}\\{{namespace}}.json",
		sort: false,
		defaultValue: (key, _namespace, _language, value) => {
			return value || key;
		},
		disablePlurals: true,
	},
});
