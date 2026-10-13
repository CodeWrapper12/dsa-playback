// 155. Min Stack
// Input
// ["MinStack","push","push","push","getMin","pop","top","getMin"]
// [[],[-2],[0],[-3],[],[],[],[]]
// Output
// [null,null,null,null,-3,null,0,-2]

class MinStack {
    private items: [number, number][] = [];

    push(val: number): void {
        const min = this.items.length ? Math.min(val, this.items[this.items.length - 1][1]) : val;
        this.items.push([val, min]);
    }

    pop(): void {
        this.items.pop();
    }

    top(): number {
        return this.items[this.items.length - 1][0];
    }

    getMin(): number {
        return this.items[this.items.length - 1][1];
    }
}
