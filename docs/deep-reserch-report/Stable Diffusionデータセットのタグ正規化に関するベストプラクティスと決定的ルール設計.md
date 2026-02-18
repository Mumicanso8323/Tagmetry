# Stable Diffusionデータセットのタグ正規化に関するベストプラクティスと決定的ルール設計

## 背景と前提

Stable Diffusion系の学習データ（LoRA / fine-tuning / DreamBoothなど）では、画像ごとに「キャプション（自然文）」または「タグ列（booru系）」を付与し、その文字列を教師信号として使うワークフローが一般的です。たとえば **kohya-ss系の学習ツールでは、画像と同名のキャプションファイル（例：`.caption`）を同一フォルダに置く形式**が説明されており、キャプションはUTF-8で「各ファイル1行」などの前提が明示されています。citeturn26view0

一方、booru（Danbooru系）では「タグ」は検索・整理のための厳格なトークンとして扱われ、**スペースはタグ区切りに使うため、タグ内のスペースはアンダースコアに置換する**、といったルールが典型です（例：`maria-sama ga miteru` → `maria-sama_ga_miteru`）。また、**タグ名の制約（カンマ不可、スペース不可、連続アンダースコア不可、ASCIIのみ等）**や、タグ種別（general/character/copyright/artist/meta）といった整理概念が提示されています。citeturn14view0turn16view0

さらに、booruタグ体系には **Tag Alias（別名統一）** と **Tag Implication（包含関係）** があり、前者は同義・表記揺れを「正準名」に寄せ、後者は「常に成り立つ包含関係」を自動付与する、という設計です。これらは「意味を保ったままタグを揃える」ための一次情報として、そのまま正規化ルール設計に転用できます。citeturn15view0turn15view1

Stable Diffusionデータセット側の現場慣習としては、**タグ列が「カンマ区切り」**で運用されるケースが非常に多く、実際にデータセット編集ツールは「タグ＝カンマで分割されるブロック」と定義しており、タグ編集・一括置換などを前提にしています。citeturn6view0turn18view0turn27view0  
この「カンマ区切り」が実務上強いのは、booruルールではそもそも**タグ名にカンマを含めない**ため、CSV風の分割が壊れにくい、という整合があるからです。citeturn14view0turn16view0

（以降では、booru由来の厳格トークン＝「booruタグ」、自然文＝「freeformキャプション」、そしてStable Diffusionのプロンプト文化で追加されがちな品質・美的トークン＝「スタイル／品質トークン」を分けて、**決定的（deterministic）**な正規化ルールを提示します。）

## 推奨の正規化パイプライン

ここでの目標は「同じ入力は常に同じ出力になる」「可逆性（少なくとも監査可能性）を保つ」「意味を落とさない」の3点です。特に“underscores vs spaces”や“masterpiece等の品質語”は、**booruタグ規約と、モデル別のプロンプト慣習が衝突しがち**なので、パイプラインは「字面の正規化」と「意味的な正規化」の二層に分けるのが安全です。citeturn14view0turn18view0turn31view0turn30view0

### 入力をまず「フィールド」と「方言」に分離する

最初に、入力文字列を次の3フィールドのどれとして扱うかを決め、**フィールドごとに異なる規則**を適用します（これが最も重要な“決定性の源泉”です）。

- **`tags.booru`**: booruタグ（正準化対象の中心）。  
- **`tags.style`**: 品質／美的／年／レーティング等の“モデル固有の制御トークン”。（後述）  
- **`caption.freeform`**: 自然文（分割せず、Unicode正規化と空白整形中心）。

この分離は、運用上は「ファイル拡張子／生成器（WD14, DeepDanbooru, BLIP等）／データセット規約」で決め打ちにするのが最も決定的です。たとえば `sd-scripts` では `.caption` をキャプションとして扱う例が示され、WD14タグ付けは `.txt` にタグ列が生成されることが明示されています。citeturn26view0turn18view0turn6view0

### 字面（文字列）正規化レイヤ

以下は **タグ・キャプション共通**に適用でき、意味を壊しにくい順序です。

- **エンコーディングと改行の正規化**: 入力はUTF-8を前提に、CRLF/LF混在を統一し、先頭BOMがあれば除去。`.caption` がUTF-8・1行前提で説明されているため、ここを固定するだけで事故が大幅に減ります。citeturn26view0turn6view0turn18view0  
- **Unicode正規化**:  
  - 原則は **NFKC**（全角英数→半角、互換文字の統一）を推奨。ただし「装飾用の互換文字」まで折り畳むので、用途によりNFCも選択肢。NFKC/NFCなどの正規形の定義はUnicode側で整理されており、Pythonでも `unicodedata.normalize('NFKC', s)` が公式に説明されています。citeturn19search0turn19search1  
- **制御文字と不可視空白の除去**: `\u0000`〜やゼロ幅文字は削除／置換（ログに残す）。  
- **空白整形**: 連続空白を1つに畳み、両端トリム。  

### トークン化（タグ列の分割）レイヤ

`tags.*` フィールドに対してのみ適用します。

- **基本セパレータは「カンマ」**を推奨（`,` または `, `）。これは現場ツールが「カンマ区切りタグ」を前提にしていること、WD14スクリプトが `caption_separator`（既定 `, `）を持つこと、`sd-scripts` 設定でも `caption_separator` が“タグ区切り文字列”として扱われ既定がカンマであること、と整合します。citeturn6view0turn18view0turn27view0  
- **代替セパレータの正規化**: `、` `，` `;` `\n` などを、ルールセットで決めた優先順位で `,` に寄せる（例：日本語入力からの混入対策）。  
- **CSV風の引用符ルール（任意）**:  
  プロンプト文化ではカンマを含むフレーズを引用符で守るようなCSV的挙動が出ることがあります（Prompt S/Rの説明で、引用符の置き方が明示されています）。ただしbooruタグ規約上「タグ名にカンマは入らない」ため、データセットが“タグ列”なら引用符対応は不要で、むしろ「引用符を削除して分割」に倒した方が決定的になります。citeturn25view0turn14view0turn16view0  

### booruタグ正準化（underscores / 大文字小文字 / 記号）

`tags.booru` では、booru規約に合わせて **「検索・一致に強い形」**に揃えます。

- **ケース**: 原則 `lowercase`（英字のみ）。booru側の例示タグ（`blonde_hair` `blue_eyes` 等）は小文字が前提で示され、検索説明もこの形で提示されます。citeturn14view1turn14view0  
- **スペース→アンダースコア**: booru規約の中核（スペースはタグ区切り、タグ内は `_`）。citeturn14view0turn14view1turn16view0  
- **アンダースコアの整形**: 連続`__`を禁止、先頭末尾`_`禁止など、規約由来の制約でバリデーション。citeturn14view0turn16view0  
- **禁止文字の扱い**: booru規約では `,` や `*` が禁止（検索ワイルドカード等と衝突）であるため、見つけたら「置換（例：全角に逃がす）」ではなく、原則は **フラグ付け**（dropせず監査）がおすすめです。citeturn14view0turn16view0  
- **曖昧性の括弧（qualifier）**: `(…)` による区別はbooru文化として明示されており、`akali_(league_of_legends)` のような形を **booruタグの一部として保持**します（これは“重み付け括弧”とは別物）。citeturn16view0turn25view0  

### プロンプト構文（重み付け括弧等）を混入させない

もし入力が「タグ列」ではなく、ユーザープロンプト由来の混在テキストなら、A1111系の **`()` / `[]` / `(word:1.5)`** のような注意機構が混じり得ます。データセット正規化では、これらは「タグ」ではなく「構文」なので、**構文を剥がして内容だけ残す**か、別フィールドに退避させるべきです（決定的ルールとしては、`(token:float)` の外側記号を除去しweightをメタデータ化、など）。これらの構文仕様とエスケープ方法はドキュメント化されています。citeturn25view0  
同様に、DeepDanbooru系ノードが「underscores→spaces」「特殊文字のescape」をオプション化している例もあり、**“どの層でunderscoreを消すか”を固定しないと、同じデータが二重変換されやすい**点に注意が必要です。citeturn17search11turn18view0  

### 重複排除と順序の扱い

- **重複排除**: 正規化後に同一になったタグは、原則「最初に現れたものを残す（stable dedupe）」が決定的で安全。  
- **順序**: “順序が生成に影響する”モデルが現実に存在するため、**ソート正規化は任意**とし、標準は「入力順保持＋必要最小の前置／後置規則」がおすすめです。たとえばNovelAIは品質タグをモデルにより“先頭に入れる／末尾に入れる”ことを明示し、順序が影響するためV3では末尾付与にしている、と説明しています。citeturn31view0turn31view1  
- ただし、モデル側が「タグ順テンプレ」を推奨する場合は、**そのテンプレに沿う並べ替えを“出力レイヤ”で行う**のが筋です（例：Animagine XL 3.1 の推奨順：`1girl/1boy, character name, series, everything else`）。citeturn30view0  

## 同義語・別名・表記揺れの統合戦略

同義語統合は、ルールの作り方を誤ると意味を壊します。決定的で安全な設計は **「外部の一次リスト（alias/implication）をスナップショットとして固定」＋「手動ルールは最小限」**です。

### Tag Aliasを“正準辞書”として使う

booruのTag Aliasは、まさに「表記揺れ・同義語・改名」を正準名に統合する仕組みとして定義されています。例として、**綴り揺れ**の `kyuubey → kyubey`、**同義語**の `clavicle → collarbone` が提示され、さらに「追加時は先行タグが削除され後続タグに置換される」といった挙動まで明文化されています。citeturn15view0

実装戦略（決定的ルール）:
- 入力タグを正規化（lower/underscore等）したのち、**alias辞書を1回だけ適用**（多段aliasは“最終正準名に張り替える”ルールが明記されているため、辞書を事前に「最終形へ圧縮」しておく）。citeturn15view0  
- alias適用は「置換（replace）」であり、「展開（expand）」ではない、と型を分ける（後述のimplicationと混同しない）。citeturn15view0turn15view1  

### Tag Implicationを“展開（補完）”として別系統に持つ

Tag Implicationは「Aなら必ずB」という包含関係で、例として `4koma ⇒ comic` `fate/stay_night ⇒ fate_(series)` が示されています。また「自動適用で上書きできないので、エッジケースに注意せよ」「同一タグ種別内のみ」などの制約も説明されています。citeturn15view1

データセット正規化での扱い（推奨）:
- **学習ラベルを増やす行為**なので、標準は「推奨タグ集合として別フィールドに計算して保持（監査可能）」  
- どうしても付与するなら「implication展開は最後に」「展開の深さ上限」「展開元を記録」などをルールセットに含め、決定性を担保。citeturn15view1turn24view1  

### スペース／アンダースコア揺れの統合

`long hair` と `long_hair` のような揺れは、booru規約上は「タグ内スペースは `_`」が正準です。citeturn14view0turn16view0  
しかし現場では、WD14等が「underscoreを消す」オプションを持ち、DeepDanbooru系ノードでもunderscores→spacesが“可読性のためのオプション”として用意され、両表現が混在し得ます。citeturn18view0turn17search11

決定的ルールとしては次が安全です。

- 正規化の内部表現は `tags.booru` を **underscore正準**に統一  
- 併せて「表示用（またはモデル別出力用）」として underscores→spaces を **出力時にのみ**行う（逆変換で情報が戻る）  
- どうしても入力側がspaces固定なら、`tags.booru`適用前に spaces→underscore を必ず通す（2重変換を避ける）citeturn14view0turn18view0turn17search11  

### 複数形・語形（pluralization）の扱い

booruのタグ付けガイドでは、**「名詞は単数形を使う」**が明示され、極端な例として「`wispberries` が山ほどあっても `wispberry` と単数でタグ付けする」と説明されています。citeturn16view0  
このため、“英語の機械的ステミング”ではなく、次の決定的ルールが推奨です。

- **基本は「alias辞書にあるplural→singular」だけを適用**（意味保持を優先）  
- 手動ルールで補う場合も、`-s`落とし等の一般化は危険なので、**ホワイトリスト型**（例：`girls→girl` は不可、`wispberries→wispberry` は可、のように）にする  
- “数タグ（`1girl`/`2girls`）”は語形ではなく**別カテゴリ（countタグ）**として扱い、plural正規化とは別ロジックに分離する（後述）。citeturn16view0turn28view0turn30view0  

### 別名リソースの入手と固定（スナップショット）

実装上は、booru本家のDBやAPIに都度問い合わせると「今日と明日で正準が変わる」ため決定性が壊れます。対策としては、**tag_aliases.json等を含むスナップショットをバージョン固定**して配布・参照するのが堅牢です。複数サイトを跨ぐ場合も、`site_tags` のように **Danbooru/Gelbooru等のタグとaliasを同梱したデータセット**が存在し、クロスプラットフォーム正規化に使える旨が説明されています。citeturn24view1

（このとき、一次辞書の出典・取得日・ハッシュをルールセットに必ず含めるのが“決定的ルール設計”の要点です。）

## 低シグナルタグの扱い（削除・フラグ・スタイル保持）

「masterpiece」「best quality」のような語は、booruの“客観性”の観点では低シグナル／主観タグに分類されやすい一方、生成モデル側では“制御トークン”として強く機能する場合があります。このねじれを、ルールで吸収します。

### booru側の原則：主観タグを避ける

Danbooru系のタグ指針では、generalタグは客観的に内容を記述すべきで、`cute` や `sexy` などの主観タグは避ける、と明記されています。citeturn14view0turn16view0  
従って、**「見た目の内容」学習を主目的**とするデータセットでは、主観・評価語の大量混入はノイズになり得ます。

### 生成モデル側の現実：品質／美的タグは“標準パート”になっている

一方で、モデル配布ページや公式ドキュメントでは、品質・美的タグを**推奨テンプレとして明示**する例があります。

- Animagine XL 3.1 は、`masterpiece, best quality, very aesthetic, absurdres` を前置きする推奨や、品質タグ・レーティングタグ・年タグ・美的タグの体系を示しています。citeturn30view0  
- NovelAIは「品質タグ追加」トグルで、モデル別に `best quality` `very aesthetic` `absurdres` 等を先頭／末尾に自動挿入し、**順序が生成に影響する**ためモデルにより末尾挿入にする、と説明しています。citeturn31view0turn31view1  
- 学習ツール側（sd-scripts設定）でも、`caption_prefix` に `"masterpiece, best quality, "` のような文字列を加える例が示され、品質語を一律前置する実装が想定されています。citeturn27view0turn30view0  

また、品質語は必ずしも“無害なノイズ”ではなく、NovelAIのモデル告知では「`masterpiece` が副作用（例：フレームが付く等）を報告されたため品質タグ体系を見直した」と述べています。つまり、これらは**スタイル／構図バイアスを持つ意味的トークン**でもあります。citeturn20search11turn31view1

### 決定的ルール：削除ではなく“分類して扱いを分岐”する

推奨ルールは「タグを3分類し、出力と学習用途で使い分ける」です。

- **Content（内容）**: 物体・属性・構図・行為など（booru general中心）  
- **Metadata（メタ）**: `rating:*`、解像度、翻訳済み等（booru meta相当）citeturn14view0turn28view0turn30view0  
- **Style/Quality（スタイル）**: `masterpiece` `best quality` `very aesthetic` `absurdres` 等（モデルごとの“制御語彙”）citeturn30view0turn31view0turn27view0  

各分類に対する“決定的アクション”の例:
- Content: 正規化して保持（alias適用・underscore正準など）  
- Metadata: 競合検出・整合性チェックの対象にする（例：`rating:general`と`rating:explicit`は同時に成立させない）citeturn28view0turn30view0  
- Style/Quality:  
  - 学習で「画質・美的」を学ばせたい（または既存モデルのプロンプト互換を保ちたい）場合は保持  
  - 内容学習や検索性重視なら `tags.style` に退避し、`tags.booru` からは除外（“削除”ではなく“分離”なので意味保持）citeturn14view0turn30view0turn31view1  

**実例（分離の必要性）**  
Animagine XLは `rating: general` を `general` に簡略化するなど、同じ概念が“別の字面”で出る可能性を示しています。ここを雑に削除すると、モデル固有の望ましい制御（safe/sensitive/nsfw など）まで落とすリスクがあります。citeturn30view0turn28view0

## 矛盾検出（Conflict Detection）と整合性ルール

矛盾検出は「完全自動で直す」より、まず **フラグ付け（監査）**し、修復はポリシーで段階化する方が安全です。booru体系の一次情報（implication/alias/blacklistメタタグ）と、学習ツール側の実務ロジック（複数人物で属性タグを削る等）を組み合わせると、決定的に設計できます。citeturn15view1turn28view0turn13view0

### 競合タイプを「ハード」と「ソフト」に分ける

- **ハード競合**: 同時に成立しない、またはシステム上1つに決める必要がある  
  - 例：`rating:general` と `rating:explicit` の同居（レーティングは単一のはず）citeturn28view0turn30view0  
  - 例：`solo` と「明確に複数人物を示す組（`1girl, 1boy`等）」の同居（少なくとも“要確認”）citeturn30view0turn28view0turn18view0  
- **ソフト競合**: 成立はし得るが、曖昧・低整合の可能性が高い  
  - 例：髪色タグが複数（`blonde_hair`と`black_hair`）—複数人物・グラデ・ツートーン等の可能性  
  - 例：`indoors` と `forest`（特殊な表現なら成立し得る）  

### “人数×属性”の曖昧性：実務で採られている決定的回避策

学習用タグの現場課題として、「複数人物がいるのに髪色・目色が複数タグ付与されると、どの人物の属性か曖昧」という問題があり得ます。`sd-scripts` に含まれるクリーニングスクリプトでは、**`girls`/`boys`が含まれる（複数人物）とき、髪色・目色などのタグが複数ある場合に削除する**、という決定的ルールが実装されています（コメントでも意図が説明されています）。citeturn13view0  
この発想は汎用性が高く、Tagmetryでも次の形で一般化できます。

- もし `count >= 2`（`2girls`/`multiple_girls`/`2boys`等が存在）なら、人物に紐づく属性群（髪色、目色、髪型、袖形状など）で **多値になったものを“曖昧”としてフラグ**、または“削除（保守的）”。citeturn13view0turn28view0turn30view0  
- もし `solo` または `1girl`/`1boy` なら、多値属性は“ソフト競合”として残し、必要に応じて `multicolored_hair` 等の補助タグを人手で追加（自動補完は別途辞書が必要）。  

### booruのメタタグ（rating等）を利用した競合検出

booru系のブラックリスト説明では、`rating:explicit` `rating:questionable` `rating:sensitive` `rating:general` といった **“タグ以外の特別コマンド（メタタグ）”**が列挙され、検索・フィルタに使う前提が示されています。citeturn28view0turn14view0  
これをデータセット正規化に転用すると、次の決定的ルールが作れます。

- `rating:*` は **多重禁止（ハード競合）**  
- モデルが `general/sensitive/nsfw/explicit` のように“簡略化レーティング”を採用する場合（Animagine XLの説明）、`rating:*` との相互変換ルールを **モデルプロファイルとして分離**（例：Animagineでは `rating: general` を `general` に寄せる）citeturn30view0turn28view0  

### “矛盾”と“欠落”を区別する（implication活用）

implicationは「AならBも必ず付く」なので、  
- AがあってBがない：**欠落（missing implied tag）**  
- Aがあって`-B`（否定）を表すような規則がある：**矛盾**  
という区別ができます。implicationは“エッジケースに注意せよ”と明記されているため、Tagmetryでは「欠落を警告」「必要なら補完を提案」に留めるのが決定的で安全です。citeturn15view1

### 具体例（入力→検出→処理のイメージ）

例として、カンマ区切りタグ列（`.txt`）を想定します。カンマ区切り運用はツール側定義とも整合します。citeturn6view0turn18view0turn27view0  

- 入力: `solo, 1girl, 1boy, blonde hair, black hair, rating:general, rating:explicit`  
- 正規化後:  
  - `tags.booru`: `solo, 1girl, 1boy, blonde_hair, black_hair`（spaces→underscore等）citeturn14view0turn16view0  
  - `tags.meta`: `rating:general, rating:explicit`（メタタグとして抽出）citeturn28view0  
- 競合:  
  - ハード：`rating:general` vs `rating:explicit`（どちらか一方に決める必要）citeturn28view0turn30view0  
  - ソフト：`solo` vs `1girl+1boy`、髪色複数（`blonde_hair`と`black_hair`）  

このときの“決定的処理”の推奨は、  
- ハード競合は **フラグ＋ポリシーに従い1つを選ぶ／両方外す**  
- ソフト競合は **フラグのみ**（学習目的により削除も可）  
です。特に複数人物の属性削除は既存ツール側にも例があり、保守的に採用できます。citeturn13view0  

## TagmetryのルールセットJSONスキーマ案（拡張可能・i18n対応）

ここでは「決定的に動くルールエンジン」を前提に、次を満たすスキーマを提案します。

- **決定性**: ルール順序・優先度・辞書スナップショットを固定できる  
- **拡張性**: 新しい“方言”（モデルプロファイル、サイト別タグ）を追加可能  
- **i18n**: 表示名・説明・例・エラー文を多言語で持てる  
- **監査性**: “何がどう変わったか”をイベントとして出力できる  

この設計は、booru側のalias/implication概念（同義統合と包含展開の分離）や、複数サイトのaliasマッピングを含むスナップショットデータ（tag_aliases.json等）を事前配布できる実務、さらに学習側で `caption_prefix` や `caption_separator` が設定可能である事実と整合します。citeturn15view0turn15view1turn24view1turn27view0turn18view0  

### JSON Schema（案）

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://tagmetry.example/schemas/ruleset.v1.json",
  "title": "Tagmetry Ruleset v1",
  "type": "object",
  "required": ["ruleset_version", "metadata", "dictionaries", "pipelines", "rules"],
  "properties": {
    "ruleset_version": { "type": "string", "pattern": "^1\\.[0-9]+\\.[0-9]+$" },

    "metadata": {
      "type": "object",
      "required": ["id", "name", "default_locale", "created_at", "frozen_at"],
      "properties": {
        "id": { "type": "string" },
        "name": { "type": "object", "additionalProperties": { "type": "string" } },
        "description": { "type": "object", "additionalProperties": { "type": "string" } },
        "default_locale": { "type": "string", "pattern": "^[a-z]{2}(-[A-Z]{2})?$" },
        "locales": {
          "type": "array",
          "items": { "type": "string", "pattern": "^[a-z]{2}(-[A-Z]{2})?$" }
        },
        "created_at": { "type": "string", "format": "date-time" },
        "frozen_at": { "type": "string", "format": "date-time" },

        "sources": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["kind", "id", "version", "sha256"],
            "properties": {
              "kind": { "type": "string", "enum": ["alias_dump", "implication_dump", "manual"] },
              "id": { "type": "string" },
              "version": { "type": "string" },
              "sha256": { "type": "string", "pattern": "^[a-f0-9]{64}$" },
              "notes": { "type": "object", "additionalProperties": { "type": "string" } }
            }
          }
        }
      }
    },

    "dictionaries": {
      "type": "object",
      "properties": {
        "aliases": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["from", "to", "scope", "source_ref"],
            "properties": {
              "from": { "type": "string" },
              "to": { "type": "string" },
              "scope": { "type": "string", "enum": ["tags.booru", "tags.style"] },
              "source_ref": { "type": "string" },
              "comment": { "type": "object", "additionalProperties": { "type": "string" } }
            }
          }
        },
        "implications": {
          "type": "array",
          "items": {
            "type": "object",
            "required": ["if", "then", "scope", "source_ref"],
            "properties": {
              "if": { "type": "string" },
              "then": { "type": "string" },
              "scope": { "type": "string", "enum": ["tags.booru"] },
              "source_ref": { "type": "string" }
            }
          }
        },
        "labels": {
          "type": "object",
          "additionalProperties": {
            "type": "object",
            "properties": {
              "display": { "type": "object", "additionalProperties": { "type": "string" } },
              "description": { "type": "object", "additionalProperties": { "type": "string" } }
            }
          }
        }
      }
    },

    "pipelines": {
      "type": "object",
      "required": ["tags.booru", "tags.style", "caption.freeform"],
      "additionalProperties": {
        "type": "array",
        "items": {
          "type": "object",
          "required": ["id", "op", "enabled"],
          "properties": {
            "id": { "type": "string" },
            "op": { "type": "string" },
            "enabled": { "type": "boolean" },
            "config": { "type": "object" },
            "on_error": { "type": "string", "enum": ["fail", "warn_and_keep", "warn_and_drop"] }
          }
        }
      }
    },

    "rules": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["id", "kind", "priority", "match", "action"],
        "properties": {
          "id": { "type": "string" },
          "kind": {
            "type": "string",
            "enum": ["normalize", "replace", "alias", "imply", "classify", "drop", "flag", "conflict"]
          },
          "priority": { "type": "integer" },
          "scope": { "type": "string", "enum": ["tags.booru", "tags.style", "caption.freeform", "*"] },

          "match": {
            "type": "object",
            "properties": {
              "type": { "type": "string", "enum": ["exact", "set", "regex", "prefix"] },
              "value": { "type": ["string", "array"] }
            }
          },

          "action": {
            "type": "object",
            "properties": {
              "type": { "type": "string", "enum": ["keep", "drop", "replace", "move", "add", "warn"] },
              "to": { "type": "string" },
              "value": { "type": ["string", "array", "object"] },
              "message": { "type": "object", "additionalProperties": { "type": "string" } }
            }
          },

          "audit": {
            "type": "object",
            "properties": {
              "emit_event": { "type": "boolean" },
              "event_type": { "type": "string" }
            }
          }
        }
      }
    }
  }
}
```

### 例ルール（実在するbooru概念の反映）

以下の例は、booruのalias/implicationの説明例や、学習現場のタグ処理オプション（underscore除去、character名の展開等）を「ルール」として落とし込む想定です。citeturn15view0turn15view1turn18view0turn30view0  

```json
[
  {
    "id": "normalize.unicode_nfkc",
    "kind": "normalize",
    "priority": 10,
    "scope": "*",
    "match": { "type": "regex", "value": ".*" },
    "action": {
      "type": "replace",
      "value": { "unicode_normal_form": "NFKC" }
    },
    "audit": { "emit_event": true, "event_type": "normalize.unicode" }
  },

  {
    "id": "alias.kyuubey_to_kyubey",
    "kind": "alias",
    "priority": 100,
    "scope": "tags.booru",
    "match": { "type": "exact", "value": "kyuubey" },
    "action": { "type": "replace", "to": "kyubey" },
    "audit": { "emit_event": true, "event_type": "alias.applied" }
  },

  {
    "id": "alias.clavicle_to_collarbone",
    "kind": "alias",
    "priority": 100,
    "scope": "tags.booru",
    "match": { "type": "exact", "value": "clavicle" },
    "action": { "type": "replace", "to": "collarbone" }
  },

  {
    "id": "imply.4koma_implies_comic",
    "kind": "imply",
    "priority": 200,
    "scope": "tags.booru",
    "match": { "type": "exact", "value": "4koma" },
    "action": { "type": "add", "value": "comic" },
    "audit": { "emit_event": true, "event_type": "imply.added" }
  },

  {
    "id": "classify.quality_tags_to_style",
    "kind": "classify",
    "priority": 50,
    "scope": "tags.booru",
    "match": { "type": "set", "value": ["masterpiece", "best quality", "very aesthetic", "absurdres"] },
    "action": {
      "type": "move",
      "to": "tags.style",
      "message": { "ja": "品質/美的トークンとしてtags.styleへ分離" }
    }
  },

  {
    "id": "conflict.rating_multiple",
    "kind": "conflict",
    "priority": 1000,
    "scope": "tags.style",
    "match": { "type": "regex", "value": "^rating:(general|sensitive|questionable|explicit)$" },
    "action": {
      "type": "warn",
      "value": { "exclusive_group": "rating", "resolution": "keep_first_warn" },
      "message": { "ja": "レーティングが複数あります（要確認）" }
    }
  }
]
```

### i18n（日本語対応）の考え方

booruの正準タグはASCII・ローマ字化が前提であることが明言されているため、Tagmetryは「内部ID（正準タグ）」と「表示名（翻訳）」を分けるのが自然です。citeturn16view0turn14view0  
一方で、複数サイトの翻訳・別名・階層を収録したデータセットが存在し、英日露などの多言語タグ分析に使える旨が説明されています。Tagmetryではこうした“翻訳辞書”もスナップショットとして固定し、`labels`辞書に取り込む設計が適します。citeturn24view1

---

## 参考としての「実世界の例」（出典由来）

- **スペース→アンダースコア**: booru規約の中核として明記（例付き）。citeturn14view0turn16view0  
- **単数形の原則**: `wispberries`でも`wispberry`、と例示。citeturn16view0  
- **aliasの具体例**: `kyuubey→kyubey`、`clavicle→collarbone`。citeturn15view0  
- **implicationの具体例**: `4koma⇒comic`、`fate/stay_night⇒fate_(series)`。citeturn15view1  
- **カンマ区切りタグ運用**: データセット編集ツールが明示し、WD14も区切り文字・underscore除去等をオプション化。citeturn6view0turn18view0turn27view0  
- **品質タグを“標準”として推奨するモデル**: Animagine XLが品質／レーティング／年／美的タグを体系として提示し、推奨前置きも明示。citeturn30view0  
- **品質タグと順序の影響**: NovelAIが自動付与タグと「順序が生成に影響する」点を明記。citeturn31view0turn31view1  
- **複数人物で属性タグを削る実務ルール**: `sd-scripts`のクリーニング実装に例がある。citeturn13view0