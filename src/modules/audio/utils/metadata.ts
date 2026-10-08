export interface AudioTrackMetadata {
	titles: string[];
	artists: string[];
	albums: string[];
	composers: string[];
	isrcs: string[];
	fileName: string;
}

export function splitMetadataValue(rawValue?: string): string[] {
	if (!rawValue || rawValue.trim() === "") return [];
	const values = rawValue
		.split(/[\n,;/，；、|\\]/)
		.map((s) => s.trim())
		.filter(Boolean);
	return Array.from(new Set(values));
}

export function collectAndSplitMetadataValues(
	raw: Record<string, string>,
	targetKeys: string[],
): string[] {
	const targetLowerKeys = new Set(targetKeys.map((k) => k.toLowerCase()));
	const collectedValues: string[] = [];

	for (const [rawKey, rawValue] of Object.entries(raw)) {
		if (targetLowerKeys.has(rawKey.toLowerCase()) && rawValue.trim()) {
			const splitValues = splitMetadataValue(rawValue);
			for (const val of splitValues) {
				if (!collectedValues.includes(val)) {
					collectedValues.push(val);
				}
			}
		}
	}

	return collectedValues;
}

export function parseAudioTrackMetadata(
	raw: Record<string, string>,
	file?: File,
): AudioTrackMetadata {
	const title = collectAndSplitMetadataValues(raw, [
		"title",
		"musicName",
		"track",
	]);

	let fileName = "";
	if (file?.name) {
		const rawName = file.name;
		const lastDotIdx = rawName.lastIndexOf(".");
		const nameNoExt =
			lastDotIdx !== -1 ? rawName.substring(0, lastDotIdx) : rawName;
		fileName = nameNoExt.trim();
	}

	const artist = collectAndSplitMetadataValues(raw, [
		"artist",
		"artists",
		"author",
		"singer",
	]);

	const album = collectAndSplitMetadataValues(raw, ["album", "albumTitle"]);

	const composer = collectAndSplitMetadataValues(raw, [
		"composer",
		"songwriter",
		"lyricist",
	]);

	const isrc = collectAndSplitMetadataValues(raw, ["isrc"]);

	return {
		titles: title,
		artists: artist,
		albums: album,
		composers: composer,
		isrcs: isrc,
		fileName,
	};
}
