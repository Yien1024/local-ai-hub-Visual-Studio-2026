def longest_palindrome(s):
    if not s:
        return ""
    best_l, best_r = 0, 0
    def expand(l, r):
        while l >= 0 and r < len(s) and s[l] == s[r]:
            l -= 1; r += 1
        return l + 1, r - 1
    for i in range(len(s)):
        for l, r in (expand(i, i), expand(i, i + 1)):
            if (r - l) > (best_r - best_l):
                best_l, best_r = l, r
    return s[best_l:best_r + 1]

for t in ["babad","cbbd","a","forgeeksskeegfor","abab"]:
    print(repr(t), "->", repr(longest_palindrome(t)))
r = longest_palindrome("ab"*100)
print("len(ab*100) result:", len(r), repr(r[:40]), repr(r[-20:]))
print("is palindrome:", r == r[::-1])
