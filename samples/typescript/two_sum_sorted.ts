// 167. Two Sum II - Input Array Is Sorted
// Input: numbers = [2,7,11,15], target = 9
// Output: [1,2]

function twoSum(numbers: number[], target: number): number[] {
    let left = 0, right = numbers.length - 1;
    while (left < right) {
        const total = numbers[left] + numbers[right];
        if (total === target) return [left + 1, right + 1];
        if (total < target) left++;
        else right--;
    }
    return [-1, -1];
}
