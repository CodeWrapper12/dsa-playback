// 146. LRU Cache, in LeetCode's JavaScript prototype style.
// Input
// ["LRUCache", "put", "put", "get", "put", "get", "put", "get", "get", "get"]
// [[2], [1, 1], [2, 2], [1], [3, 3], [2], [4, 4], [1], [3], [4]]
// Output
// [null, null, null, 1, null, -1, null, -1, 3, 4]

/**
 * @param {number} capacity
 */
var LRUCache = function(capacity) {
    this.capacity = capacity;
    this.items = new Map();
};

/**
 * @param {number} key
 * @return {number}
 */
LRUCache.prototype.get = function(key) {
    if (!this.items.has(key)) return -1;
    const value = this.items.get(key);
    this.items.delete(key);
    this.items.set(key, value);
    return value;
};

/**
 * @param {number} key
 * @param {number} value
 * @return {void}
 */
LRUCache.prototype.put = function(key, value) {
    this.items.delete(key);
    this.items.set(key, value);
    if (this.items.size > this.capacity) this.items.delete(this.items.keys().next().value);
};
