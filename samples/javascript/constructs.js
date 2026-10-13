// Every construct the visualizer has to instrument without changing what the code does.
// Input: nums = [5,3,8,1,9,2], word = "abc"
// Output: "1,2,3,5,8,9|cba|7|x:3|odd,odd,even|found|done"

/**
 * @param {number[]} nums
 * @param {string} word
 * @return {string}
 */
var constructs = function(nums, word) {
    const sorted = [...nums].sort((a, b) => a - b);
    for (let i = 0, j = sorted.length - 1; i < j; i++, j--) [sorted[i], sorted[j]] = [sorted[j], sorted[i]];
    sorted.reverse();
    let reversed = "";
    for (const ch of word) reversed = ch + reversed;
    let total = 0;
    outer: for (let i = 0; i < 3; i++) {
        for (let j = 0; j < 3; j++) {
            if (j > i) continue outer;
            total++;
        }
    }
    const point = { x: 3, get double() { return this.x * 2; } };
    const kinds = [];
    for (const n of [1, 3, 4]) {
        switch (n % 2) {
            case 0: kinds.push("even"); break;
            default: kinds.push("odd");
        }
    }
    let found = "missing";
    try {
        if (sorted.indexOf(9) >= 0) throw new Error("found");
    } catch (e) {
        found = e.message;
    } finally {
        total += 1;
    }
    let text = "";
    text += sorted.join(",");
    text += `|${reversed}|${total}|x:${point?.x}|${kinds}|${found}`;
    if (point.double === 6) text += "|done";
    return text;
};
