
export class LRUCache<K, V> {
	private maxSize: number;
	private map: Map<K, V>;
	private onEvict: ((key: K, value: V) => void) | null;

	constructor(
		maxSize: number,
		onEvict: ((key: K, value: V) => void) | null = null,
	) {
		this.maxSize = maxSize > 0 ? maxSize : 1;
		this.map = new Map<K, V>();
		this.onEvict = onEvict;
	}

	get(key: K): V | undefined {
		const entry = this.map.get(key);
		if (entry) {
			this.map.delete(key);
			this.map.set(key, entry);
		}
		return entry;
	}

	set(key: K, value: V) {
		if (this.map.has(key)) {
			const existingEntry = this.map.get(key);
			if (existingEntry) {
				this.onEvict?.(key, existingEntry);
			}
			this.map.delete(key);
		}

		this.map.set(key, value);

		if (this.map.size > this.maxSize) {
			this.evict();
		}
	}

	private evict() {
		const lruKey = this.map.keys().next().value;
		if (lruKey) {
			const lruEntry = this.map.get(lruKey);
			if (lruEntry) {
				this.onEvict?.(lruKey, lruEntry);
			}
			this.map.delete(lruKey);
		}
	}

	clear() {
		if (this.onEvict) {
			for (const [key, entry] of this.map.entries()) {
				this.onEvict(key, entry);
			}
		}
		this.map.clear();
	}
}
