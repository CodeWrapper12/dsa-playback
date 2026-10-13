// 146. LRU Cache, kept in order by an array: correct, but each call scans it.
// Input
// ["LRUCache", "put", "put", "get", "put", "get", "put", "get", "get", "get"]
// [[2], [1, 1], [2, 2], [1], [3, 3], [2], [4, 4], [1], [3], [4]]
// Output
// [null, null, null, 1, null, -1, null, -1, 3, 4]

class LRUCache {
    private values = new Map<number, number>();
    private order: number[] = [];

    constructor(private capacity: number) {}

    get(key: number): number {
        if (!this.values.has(key)) return -1;
        this.order.splice(this.order.indexOf(key), 1);
        this.order.push(key);
        return this.values.get(key)!;
    }

    put(key: number, value: number): void {
        if (this.values.has(key)) this.order.splice(this.order.indexOf(key), 1);
        else if (this.values.size === this.capacity) this.values.delete(this.order.shift()!);
        this.order.push(key);
        this.values.set(key, value);
    }
}
