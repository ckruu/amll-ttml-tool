import { enableMapSet } from "immer";
import { Provider } from "jotai";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import "react-toastify/dist/ReactToastify.css";
import App from "./App.tsx";
import "./i18n/index.ts";
import "./index.css";
import "./utils/pwa.tsx";
import { globalStore } from "./states/store.ts";

async function startApp() {
	enableMapSet();
	const rootEl = document.getElementById("root");

	if (!rootEl) {
		throw new Error("Could not find root element");
	}

	createRoot(rootEl).render(
		<StrictMode>
			<Provider store={globalStore}>
				<App />
			</Provider>
		</StrictMode>,
	);
}

startApp();
