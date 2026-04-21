# Protocol Singularity — GitHub Pages デプロイ手順

## 全体の流れ

```
1. main に push
2. Unity ライセンスを取得して Secret に登録 (初回のみ)
3. Mercury2 API キーを Secret に登録
4. Pages を有効化
5. deploy workflow が自動で走る → 公開完了
```

---

## ステップ 1: main に push

```bash
cd C:\Users\nc271\UnityDocument\Protocol_Singularity
git add .
git commit -m "Initial commit"
git push -u origin main
```

この時点でリポジトリに `.github/workflows/` が公開され、Actions タブが使えるようになる。

---

## ステップ 2: Unity ライセンスを取得 (初回のみ)

Unity 6 で WebGL ビルドを走らせるには Personal License が必要。無料だが、CI 用には `.ulf` ファイルを取得して Secret に登録する必要がある。

### 2-1. `.alf` リクエストを生成

1. GitHub のリポジトリ → **Actions** タブ
2. 左サイドバーから **"Request Unity Activation File"** を選択
3. 右上の **"Run workflow"** ボタン → 緑の **"Run workflow"** をクリック (branch は main のまま)
4. 1-2 分で workflow が完了。クリックして開く
5. 下の **Artifacts** セクションに `unity-activation-file` というファイルがある → ダウンロード
6. ZIP を展開すると `Unity_v6000.4.1f1.alf` のようなファイルが出てくる

### 2-2. `.ulf` (ライセンス) を取得

1. https://license.unity3d.com/manual をブラウザで開く
2. Unity ID でログイン (普段 Unity Editor で使っているアカウント)
3. 先ほどダウンロードした `.alf` ファイルをアップロード
4. ライセンス種別を選ぶ画面 → **Unity Personal** を選択 → 同意にチェック → Next
5. `Unity_v6000.x.ulf` がダウンロードされる

### 2-3. Secret に登録

1. GitHub のリポジトリ → **Settings** → **Secrets and variables** → **Actions** → **"New repository secret"**
2. 以下 3 つを登録:

   | Name | Value |
   |---|---|
   | `UNITY_LICENSE` | `.ulf` ファイルの**中身をテキストエディタで開いて全コピーして貼り付け** |
   | `UNITY_EMAIL` | Unity ID のメールアドレス |
   | `UNITY_PASSWORD` | Unity ID のパスワード |

   ※ Personal License なら `UNITY_SERIAL` は不要。Pro/Plus のシリアルがある場合のみ追加。

これで Unity 側の準備は完了。2-1 の activate workflow は 2 度目以降は走らせなくて OK。

---

## ステップ 3: Mercury2 API キーを登録

同じく Settings → Secrets → Actions で以下を登録:

| Name | Value |
|---|---|
| `MERCURY2_API_KEY` | Inception Labs の API キー (必須) |
| `MERCURY2_ENDPOINT` | 省略時 `https://api.inceptionlabs.ai/v1` |
| `MERCURY2_MODEL` | 省略時 `mercury-2` |

`MERCURY2_API_KEY` が空でもビルドは通るが、CPU はランダム挙動にフォールバックする。

---

## ステップ 4: GitHub Pages を有効化

1. リポジトリ → **Settings** → **Pages**
2. **Source** を **"GitHub Actions"** に変更 (Deploy from a branch ではなく)

---

## ステップ 5: deploy workflow を走らせる

- main に push するたびに自動で走る
- 手動で走らせたい場合: Actions → **"Build WebGL & Deploy to GitHub Pages"** → Run workflow

初回ビルドは Library キャッシュが空なので 20-30 分かかる。以降はキャッシュヒットで 5-10 分。

完了すると公開 URL が表示される: `https://spicycurrydummy-cloud.github.io/Protocol-Singularity/`

---

## トラブルシューティング

**Unity License activation error (CI 上で license-related エラー)**
- `UNITY_LICENSE` に貼った内容を確認。`.ulf` の XML を先頭から末尾まで全部コピーする (途中の改行も含めて)
- `UNITY_EMAIL` / `UNITY_PASSWORD` も正しいか確認

**WebGL がブラウザで動かない / loading で止まる**
- F12 Console でエラー確認
- Brotli の解凍エラーが出る場合は `ProjectSettings/ProjectSettings.asset` の `webGLDecompressionFallback: 1` を確認

**Mercury2 API が呼ばれない**
- Web Console で `[Mercury2]` ログを確認
- `MERCURY2_API_KEY` が登録されているか確認

---

## 注意

- **`Assets/StreamingAssets/mercury2-config.json` はコミットしない** (`.gitignore` で除外済み)
- **`.alf` / `.ulf` ファイルもコミットしない** (`.gitignore` で除外済み)
- Unity License は 2 年で期限切れ。切れたら同じ手順で再取得
