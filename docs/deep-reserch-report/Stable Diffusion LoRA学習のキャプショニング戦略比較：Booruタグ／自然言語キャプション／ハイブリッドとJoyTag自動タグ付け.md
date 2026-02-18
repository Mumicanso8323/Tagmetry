# Stable Diffusion LoRA学習のキャプショニング戦略比較：Booruタグ／自然言語キャプション／ハイブリッドとJoyTag自動タグ付け

## エグゼクティブサマリー

Stable Diffusion系のLoRA微調整において、キャプション（= 学習時の「プロンプト」）の設計は、(a) 概念再現度（concept fidelity）、(b) スタイル転写（style transfer）、(c) プロンプト頑健性（prompt robustness）の三者トレードオフを支配します。Stable Diffusionは凍結したCLIP系テキストエンコーダの埋め込みで生成を条件付けるため、キャプション設計は“テキスト条件付けの分布”そのものを決めることになります。citeturn5search0turn5search2turn10search0

結論を先にまとめると、次の傾向が（学術的前提＋実装仕様＋コミュニティ実務の整合）として堅いです。citeturn10search0turn5search0turn22view0turn13view0turn20search2

- **Booruタグ列（Danbooru風）**は、「狙った属性をトークンとして明示」しやすく、**概念再現度・局所属性制御**に強い一方、「自然文でプロンプトしても効く」タイプの頑健性は弱くなりがちです（学習分布が“タグ言語”に寄るため）。citeturn12search15turn22view0turn13view0turn5search1turn9search21  
- **自然言語キャプション（英語中心）**は、「文脈・関係・自然文プロンプト」への頑健性を作りやすい一方で、キャプション品質がブレると**ノイズ学習／概念の結び付きの曖昧化**が起きやすいです（自動生成キャプションは特に）。citeturn7search0turn7search1turn5search1turn9search21  
- **ハイブリッド（タグ＋短文／確率的切替）**は、設計さえ良ければ三者のバランスが最も取りやすいですが、**「順序」「シャッフル」「ドロップアウト」「トリガー語の固定」**を誤ると、逆に不安定化します（学習実装が“カンマ分割→並べ替え／間引き”を行うため）。citeturn22view0turn20search2turn15view1turn23search0turn9search21  

そして **JoyTag系自動タグ付け**は、「Booruタグ列」または「ハイブリッドのタグ側」を作るための高性能な自動化コンポーネントとして位置付けるのが素直で、Danbooruスキーマ／マルチラベル分類として学習データにタグを付与する用途を明確に想定しています。citeturn13view0turn12search15turn15view0

本レポートでは、(1) 3方式のトレードオフ、(2) 順序・レアトークン・トリガー語の設計、(3) Tagmetry向け「caption policy」提案（SD1.5/SDXLプリセット含む）、(4) 具体例（日本語・英語）と意思決定ツリー（mermaid）を、実装仕様（kohya-ss系 / diffusers系）に合わせて整理します。citeturn22view0turn6search3turn5search0turn8search3turn8search10

---

## 背景と評価軸

### キャプションは「学習時プロンプト分布」そのもの

Stable Diffusion（SD1.x系）は、画像生成UNetが**凍結されたCLIP ViT-L/14テキストエンコーダの非プール出力**（トークン列の埋め込み）で条件付けされ、クロスアテンションを通じて生成が誘導されます。citeturn5search0turn5search2turn10search9turn10search0  
つまりLoRA学習におけるキャプションは、「このLoRAが反応すべきテキスト条件」を直接定義します。

SDXLはこれを拡張し、**2つの固定テキストエンコーダ（OpenCLIP-ViT/G と CLIP-ViT/L）**を用いる前提が公式に示されています。citeturn6search0turn6search3turn6search2  
この差により、「タグ語彙」「自然文」「二重プロンプト（prompt_2）」の扱いが実務上変わります。citeturn6search13turn22view0turn23search9

### 77トークン制約が「順序・圧縮・トリガー固定」を必須にする

CLIP系テキストエンコーダは（既定で）**context_length=77**の制約を持ち、長い入力は切り捨て（または実装によってはチャンク化）されます。citeturn9search21turn5search1turn9search25turn23search0  
この制約は、学習と推論の両方で「重要トークンを前方に置く」必要性を生みます。kohya-ss系の学習ユーティリティ実装では、キャプションを処理する際に**カンマ区切りでトークン（=タグ断片）を固定部と可変部に分け、可変部をシャッフルし、タグ単位にドロップアウト**するロジックが明示されています。citeturn22view0turn20search2  
同実装は長トークン列をチャンク化する処理も含み、77制約や分割処理に実務が依存していることが分かります。citeturn22view0turn23search0turn23search23

### 評価軸の定義（本レポート）

- **概念再現度（Concept fidelity）**：トリガー語や指定語で、対象（人物・キャラ・物体・衣装など）が一貫して再現される強さ。citeturn10search2turn15view1turn22view0  
- **スタイル転写（Style transfer）**：線・塗り・質感・画風などが、他の内容にも“乗る”強さ。LoRAは一般に「特定概念」だけでなく「スタイル適応」でも用いられることが前提です。citeturn9search3turn8search10turn22view0  
- **プロンプト頑健性（Prompt robustness）**：単語の言い換え、順序、冗長化、部分重み付け、自然文 vs タグ言語の違い、SD1.5/SDXLや各種UIの違い（prompt weightingや長文処理）に対して「壊れにくい」性質。citeturn23search9turn23search0turn22view0turn6search13  
- **データセット規模／品質**：ここではユーザー条件が「特に制約なし」なので、一般論として「枚数が少ないほどキャプションのノイズ耐性が重要」「枚数が多いほど語彙管理と正規化が重要」という形で扱います。citeturn10search2turn7search0turn13view0turn12search15  
- **トークナイゼーション／レアトークン**：トリガー語（identifier）が既存語彙と衝突しない／できれば1トークンであること、77制約で切れない配置など。citeturn15view1turn10search2turn9search21turn22view0  
- **過学習リスク**：小規模データや過度に決め打ちのキャプションで「背景・構図・衣装が固定化」「言語ドリフト」が起きる。DreamBooth系文脈ではclass priorを保つ工夫（正則化画像など）が明確に議論されています。citeturn10search2turn15view1turn22view0  
- **SD1.5 vs SDXL一般化**：アーキテクチャ差（テキストエンコーダ数など）により、同一LoRAの互換性は自明ではありません（他バージョン互換を狙う研究が存在すること自体が、非互換が課題である示唆）。citeturn6search0turn6search3turn28academia4  

---

## キャプション方式の比較分析

### 方式の定義

- **A：Booruタグ列**：`1girl, blue_hair, long_hair, smile, ...` のように、Danbooru系のタグ語彙で列挙する。citeturn12search15turn13view0turn15view0  
- **B：自然言語キャプション**：`a girl with long blue hair smiling under cherry blossoms` のように文で記述する（自動キャプションはBLIP/BLIP-2やVLM/LLMベースが代表）。citeturn7search0turn7search1  
- **C：ハイブリッド**：タグ列＋短文、または「タグ版/自然文版を確率的に切替」などで併用する（学習実装のシャッフル・ドロップアウトと整合させる必要あり）。citeturn22view0turn20search2turn15view1  

JoyTagは「Danbooruタグスキーマでのマルチラベル画像タグ付け」を明示し、手描きから写真まで幅広い入力に対してタグ出力を返す用途（= AまたはCのタグ側の自動生成）に自然に組み込まれます。citeturn13view0

### 方式別トレードオフ（要件：概念再現度・スタイル転写・プロンプト頑健性）

下表は、LoRA“利用者”の観点（推論時のプロンプトで制御できるか）に寄せた整理です。なお「高/中/低」は相対評価で、実際はデータ品質・学習設定・ベースモデル（SD1.5/SDXL/派生）で変わります。citeturn5search0turn6search3turn22view0turn7search0turn13view0  

| 観点 | A：Booruタグ列 | B：自然言語キャプション | C：ハイブリッド（タグ＋短文／確率切替） |
|---|---|---|---|
| 概念再現度（特定の属性を出す） | **高**：属性をトークンとして明示しやすい。77制約下でも重要語を前方に置ける。citeturn22view0turn9search21 | 中〜高：文で書くと“属性の粒度”が揺れやすい（自動キャプションは特に）。citeturn7search0turn7search1 | **高**：タグで粒度を担保し、短文で文脈頑健性を補う。ただしトリガー固定が必須。citeturn22view0turn15view1 |
| スタイル転写（他の内容にも乗る） | 中：スタイルをタグとして表せるが、背景/構図タグを多く入れると“スタイル”ではなく“シーン丸ごと”に吸着しやすい。citeturn15view1turn22view0 | **高**：文脈・関係・抽象表現（“in the style of …”など）を学習分布に入れやすい。citeturn10search2turn7search0 | **高**：スタイル部分を短文で、描写部分をタグで、と分業しやすい。citeturn22view0turn23search9 |
| プロンプト頑健性（言い換え・順序・UI差） | 低〜中：タグ言語に最適化されやすく、自然文での言い換えに弱いことが多い（学習分布の問題）。citeturn22view0turn23search9 | **高**：自然文プロンプトと分布が近いほど頑健。citeturn7search1turn23search9 | **高（狙える）**：タグシャッフル＋短文混在で頑健性を作れるが、設計ミスで逆効果。citeturn22view0turn20search6 |
| データ品質要求 | 中：タグの誤検出・過多タグがノイズになるので“削る作業”が重要。citeturn13view0turn15view0 | **高**：キャプションの文法/意味のブレがそのままノイズ。BLIP系はノイズ低減（フィルタ）を研究課題として扱う。citeturn7search0turn7search1 | **高**：二系統の品質管理が必要（ただし“短文は短く”で運用負荷を下げられる）。citeturn22view0turn9search21 |

### “JoyTag/WD14/BLIP-2”はどこに入るか（実務フロー）

A/B/Cは「キャプションの言語」ですが、現場では多くの場合「自動生成→人手修正→学習」というパイプラインになります。citeturn15view0turn13view0turn7search0turn7search6  

- **JoyTag（Danbooruスキーマ、5000+タグ、Danbooru2021等で学習）**：Booru系タグ列を得る用途に強い。特に「画像にテキスト対が無い」データを拡散モデル学習用にタグ付けする文脈が明示されています。citeturn13view0turn12search15  
- **WD14 Tagger（kohya-ssのタグ付けスクリプト／ONNX推奨）**：学習用タグファイル生成の“実装としての標準”に近く、`--thresh`、`--remove_underscore`、`--undesired_tags`、`--character_tags_first`、`--always_first_tags`等の運用上重要なスイッチを持ちます。citeturn15view0  
- **BLIP/BLIP-2**：自然言語キャプションを作る側。BLIPは「ノイズの多いWebテキストはサブオプティマル」である点を明示し、合成キャプション生成＋フィルタ（CapFilt）を提案しています。citeturn7search0turn7search4  
- **“GPTベース/LLMベースのキャプション”**：BLIP-2は凍結LLMを利用した視覚→言語生成を含む設計で、自然言語の豊かさを引き出しやすい一方、LoRA学習用途では「一貫した語彙・短さ・誤りの少なさ」を別途担保する必要があります。citeturn7search1turn7search5  

---

## タグ順序・レアトークン・トリガーワード設計

この章は、指定要件(2)「順序（タグ順/ランダム化）、レアトークン配置、トリガーワード設計」を、**実装仕様に沿って**具体化します。citeturn22view0turn15view1turn9search21turn23search0

### タグ順序は“意味論”と“実装”の両方に効く

1. **意味論（CLIPは順序を持つ）**：CLIPテキストエンコーダはトランスフォーマであり、入力トークン列は位置埋め込みを持ちます。したがって理屈の上では語順や前後文脈が埋め込みに影響します。citeturn9search0turn9search21  
2. **実装（77トークン＋シャッフル/ドロップ）**：kohya-ssの学習実装では、キャプションを  
   - prefix/suffixを付与し、  
   - caption_dropout（キャプション全体を空にする）や、  
   - shuffle_caption / keep_tokens / keep_tokens_separator / caption_tag_dropout_rate でカンマ分割の“タグ断片”を操作し、  
   - その後にトークナイズ（77制約）  
   という順番で処理します。citeturn22view0turn20search2  

この構造により、**「重要な概念（トリガー、対象クラス、絶対に外したくない属性）」を“固定領域”に置く**ことが最重要になります。citeturn22view0turn15view1turn9search21  

### 実務ガイド：固定部・可変部の切り方（推奨パターン）

kohya-ss実装が提供する固定化方法は大きく2つです。citeturn22view0turn20search2  

- **方法A：keep_tokens（先頭N個を固定）**  
  - 例：`keep_tokens=2`なら、先頭2つの“カンマ区切り断片”はシャッフルされません。citeturn22view0turn20search2  
- **方法B：keep_tokens_separator（セパレータで固定部/可変部/固定サフィックスを分離）**  
  - 実装上、`keep_tokens_separator`が存在すると、`固定部 || 可変部 || 固定サフィックス` の3分割が可能で、固定サフィックス（例：画質タグや“必ず末尾に置きたい語”）も保持できます。citeturn22view0turn27search3turn27search5  

**結論（運用推奨）：**  
- トリガー語や“そのLoRAを呼び出す識別子”は **固定部**（できれば先頭）に置く。citeturn15view1turn10search2turn22view0  
- “可変にしたい属性（服・表情・背景・構図など）”は **可変部**に置き、シャッフルやタグドロップで頑健性を作る。citeturn22view0turn20search6turn28search1  
- “環境依存の固定タグ（例：常に同じ背景が写ってしまう）”は、**タグとして入れるか／逆に消すか**が目的依存で分かれる（後述の決定木で分岐）。citeturn15view1turn28search0turn3search0  

### シャッフルとドロップアウトは“頑健性のための正規化”として使う

kohya-ss実装上、以下が明示的に行われます。citeturn22view0turn20search2  

- `shuffle_caption`：可変部トークン（カンマ区切り）をシャッフル。citeturn22view0turn20search2  
- `caption_tag_dropout_rate`：タグ単位で一定確率で除去。citeturn22view0turn20search0  
- `caption_dropout_rate` / `caption_dropout_every_n_epochs`：キャプション全体を空にする（= 無条件に近い学習を混ぜる）。citeturn22view0turn20search0  

コミュニティ的にも「caption_tag_dropout_rate と shuffle の方がバランスを取りやすい」という経験則が語られています（ただし“キャラ/概念”ではcaption_dropoutは悪影響になり得る、等の注意も含む）。citeturn20search6turn15view1  

**実務的な解釈：**  
- シャッフル＝「順序への過適合」を防ぎ、同じ集合でも違う並びで提示して“順序不変に近い挙動”を誘導する。citeturn22view0turn23search0  
- タグドロップ＝「常に同時に出るタグの結び付きを弱める」（背景や小物のリーク抑制に使える）。citeturn22view0turn28search1turn3search0  

### レアトークン（トリガー語）の設計：衝突回避と1トークン性

DreamBooth文脈では「固有のidentifierを、クラス名と組にして学習し、生成時にそのidentifierで呼び出す」という設計が中心で、kohya-ssの共通ドキュメントも同様の説明をしています。citeturn10search2turn15view1  
このドキュメントは、identifierについて「トークナイザで1トークンになる3文字以下でレアな単語が良い」という推奨を明示しています。citeturn15view1turn9search21turn9search25  

**トリガー語の実務ルール（推奨）：**

- **既存語彙・一般名詞を避ける**：モデルが元々知っている単語（人名・よくある単語）だと、既存概念と混ざるリスクが上がる（衝突）。citeturn10search2turn15view1  
- **“覚えやすさ”と“衝突しにくさ”を両立**：例えば「人名＋サフィックス（v1等）」「英数字混在」などがコミュニティで提案されます。citeturn28search1turn15view1  
- **配置は先頭（固定部）**：77トークン制約の切り捨てや、シャッフル/ドロップの影響を受けないようにします。citeturn9search21turn22view0turn23search0  

### Booruタグの“カテゴリ順”は何を意味するか

Danbooru系タグは一般に **general / artist / copyright / character / meta** といったカテゴリ分類を持つ（派生実装やツールもこの分類を前提にする）ことが広く共有されています。citeturn12search0turn12search7  
ただし、LoRA学習でどのカテゴリを入れるかは目的依存です。

- **キャラクタ再現（固有キャラ）**：characterやcopyrightを入れると“呼び出し語”として便利な場合があるが、配布・汎用性の観点では「固有名詞をどこまで入れるか」を慎重に決める必要があります。citeturn15view1turn28search1turn13view0  
- **スタイルLoRA**：artistタグ（画風）に相当するタグを“スタイル識別子”として使う運用もあり得ますが、既存の画家名や作家名は倫理・権利・混同の論点も絡むため、ここでは純粋に技術的観点（衝突回避）に留めます。citeturn15view1turn10search2  

---

## SD1.5とSDXLにおける一般化と学習レシピ

### SD1.5とSDXLの差分がキャプション戦略に与える影響

SD1.xはCLIP ViT-L/14での条件付けが明示されます。citeturn5search0turn5search2  
一方SDXLは2つの固定テキストエンコーダ（OpenCLIP-ViT/G + CLIP-ViT/L）を使うことが公式に説明され、diffusersでも第二プロンプト（prompt_2）を別エンコーダへ送れるAPIが提供されています。citeturn6search0turn6search3turn6search13  

| 項目 | SD1.5（SD1.x系） | SDXL |
|---|---|---|
| テキストエンコーダ | CLIP ViT-L/14（凍結）citeturn5search0turn5search2 | 2系統（OpenCLIP-ViT/G + CLIP-ViT/L）citeturn6search0turn6search3 |
| プロンプト経路の分岐 | 基本1系統 | `prompt` と `prompt_2` を分けられる（実装依存）。citeturn6search13 |
| キャプション設計の含意 | 「77トークン内に重要語を入れる」「タグ語彙 or 自然文のどちらに寄せるか」を明確化。citeturn9search21turn22view0 | 「タグ言語＋自然文を“別経路”に分ける」余地がある（特に推論時）。citeturn6search13turn23search9 |
| 互換性（LoRAの流用） | 同系統派生チェックポイント間で“動くことが多い”が保証ではない（推論）。citeturn9search3turn5search0 | SD1.x LoRAをそのままSDXLへ、は原理的に非自明。プラグイン互換を課題とする研究がある。citeturn28academia4turn6search0 |

### 学習レシピ：目的別に「キャプション方式」と「学習方式」を揃える

kohya-ssの共通ドキュメントは、LoRAを含む学習において、少なくとも以下の指定方式を整理しています。citeturn15view1turn20search2  

- **DreamBooth class+identifier方式**：キャプション不要で簡単だが、“画像中の全要素がidentifierに紐付く”ため、服や背景が変えづらくなるリスクが明示されています。citeturn15view1turn10search2  
- **DreamBooth キャプション方式**：画像ごとに詳細を書き分け、キャラと衣装等を分離して学習できる、という期待が明示されています。citeturn15view1turn10search2  
- **fine-tuning方式（メタデータ管理）**：タグ/キャプションの管理やキャッシュ等、運用上の機能を取り込みやすい。citeturn15view1turn20search2  

**本レポートの主題（キャプショニング戦略）**は基本的に「DreamBooth キャプション方式」または「fine-tuning方式」で効きます。

### 過学習・言語ドリフトを抑えるための正則化（特に概念LoRA）

DreamBooth論文は、少数画像の微調整でクラス概念が崩れる（language drift）ことを問題化し、クラス固有の事前分布を保つ工夫（prior preservation）を導入しています。citeturn10search2turn10search6  
kohya-ssの共通ドキュメントも、正則化画像を使わない場合に `1girl` 生成が学習対象に寄っていく、という説明を含めます。citeturn15view1  

**キャプション戦略との接続**：  
- 正則化画像を用いる場合、キャプション（またはclass_tokens）設計が“クラス概念を保つ”役割を持つため、トリガー語とクラス語の配置・固定はより重要になります。citeturn15view1turn22view0  

### 推論時プロンプト設計：学習した「言語」に合わせる

推論時のプロンプト重み付け・長文処理・二重プロンプト等は、UI/ライブラリで差があります。diffusersは「prompt_embeds を通じた重み付け」「Compel等の利用」を案内しており、長文（77超）や重み付けに関するドキュメント・議論が存在します。citeturn23search9turn23search16turn23search0  

- **タグ学習LoRA（A）**：推論もタグ言語（短い列挙）で書く方が一致しやすい。77制約下では“重要タグ＋トリガー”を前方へ。citeturn9search21turn22view0  
- **自然文学習LoRA（B）**：推論は短い自然文（英語中心）を主にし、タグ列挙を最小化する方が分布一致しやすい。citeturn7search1turn5search2  
- **ハイブリッド（C）**：推論でも「タグ＋短文」または、SDXLでは `prompt` と `prompt_2` を分離して“タグ側/自然文側”を担わせる戦略が取れます（有効性はモデル依存なので実験前提）。citeturn6search13turn6search3turn23search9  

---

## Tagmetry向け「caption policy」提案

本調査では、Stable Diffusion LoRA学習向けの **Tagmetry**（ユーザーが指すツール）の一次ドキュメントを確認できなかったため、ここでは「**kohya-ss/diffusersで再現できる挙動**」に合わせた **ポリシー仕様の提案**として記述します。中核となる操作（固定部・シャッフル・タグドロップ・ワイルドカード）は、kohya-ss実装とドキュメントに根拠があります。citeturn22view0turn20search2turn15view0turn15view1turn8search3  

### デフォルト方針（Tagmetry “caption policy”の設計原則）

**原則：**「トリガーを常に固定」「可変にしたい要素だけをタグ化」「順序依存を減らすためにシャッフル」「過学習を抑えるためにタグドロップ」「自然文頑健性は“短文の混在”で作る」。citeturn22view0turn15view1turn20search6turn7search0turn9search21  

JoyTag/WD14は“タグ側”の自動生成器として扱い、出力をそのまま学習に入れるのではなく、**不要タグ除去・閾値調整・固定タグ付与**（例：always_first_tagsで `1girl` を先頭に寄せる等）をポリシーで管理します。citeturn13view0turn15view0  

### Tagmetryポリシーのフォーマット例（提案）

以下は「TagmetryがTOML/YAMLでポリシーを受け取り、学習用`.txt`キャプションを生成する」想定の例です（**提案であり公式仕様ではありません**）。  
根拠となるパラメータ名は、kohya-ss側の概念（keep_tokens / shuffle_caption / caption_tag_dropout_rate / enable_wildcard / caption_prefix 等）に対応させています。citeturn22view0turn20search2turn27search5  

#### デフォルト：Hybrid-Anchor（推奨）

```toml
# tagmetry_policy_default.toml  （提案フォーマット）

[caption_policy]
name = "hybrid-anchor-v1"
language_primary = "en"      # 学習の主言語（推奨：英語）
language_secondary = ["ja"]  # 併記する場合（任意）

# 1) トリガー語を必ずキャプション先頭に入れる（固定部）
#    kohya-ss側では caption_prefix / keep_tokens / keep_tokens_separator で同等のことを実現できる
trigger_token = "sks"
caption_prefix = "sks, "     # 例：常に先頭に置く

# 2) タグ生成（JoyTag / WD14など）
[tagging]
engine = "joytag"            # or "wd14"
threshold = 0.40             # JoyTag READMEの例に合わせた初期値
max_tags = 40                # 77トークン制約を意識し上限を設ける
remove_underscore = false    # Danbooru風を維持（必要ならtrue）
undesired_tags = ["rating:*", "signature", "watermark"]  # 例（要調整）

# 3) タグ整形
[tags]
# 重要タグ（固定部）候補：クラス語・主対象
# 例：1girl/1boy、キャラ名、シリーズ名、コア属性
always_first = ["1girl"]
core_tags = ["solo"]                 # 必要なら
drop_if_constant = ["background"]    # “常に同じで学ばせたくない”もの（例）

# 4) ハイブリッド化：タグ列＋短文（英語/日本語）を複数行で出力
#    kohya-ssの enable_wildcard + 複数行キャプション（ランダムに1行選択）を想定
[hybrid]
mode = "multiline_random"
lines = [
  "{TAGS}",                               # 行1: タグ列
  "{EN_SENTENCE}",                        # 行2: 短い英語文（コンマを避ける）
  "{JA_SENTENCE}"                         # 行3: 短い日本語文（任意）
]

# 5) 学習側（kohya）に合わせた “頑健化” 推奨値（Tagmetryが設定ファイルも吐く想定）
[train_side_recommendation_kohya]
shuffle_caption = true
keep_tokens = 1                 # トリガー(sks)だけ固定
caption_tag_dropout_rate = 0.10 # 可変タグの10%をランダムに落とす
caption_dropout_rate = 0.00     # 概念LoRAでは原則オフ
enable_wildcard = true
```

#### SD1.5プリセット（tag-heavy）

SD1.5はCLIP ViT-L/14（77トークン）での条件付けが中心で、タグ列の短さと固定部設計の重要度が高いので、デフォルトより“タグ側”比率を上げます。citeturn5search0turn5search1turn9search21turn22view0  

```toml
# tagmetry_policy_sd15.toml（提案フォーマット）

[caption_policy]
name = "sd15-tag-heavy-v1"
trigger_token = "sks"
caption_prefix = "sks, "

[tagging]
engine = "wd14"         # kohya系運用との親和性を優先
threshold = 0.35
max_tags = 35
remove_underscore = true

[hybrid]
mode = "multiline_random"
# タグを2行分“重複”させ、自然文の出現確率を下げる（均等ランダムを前提とした擬似確率制御）
lines = ["{TAGS}", "{TAGS}", "{EN_SENTENCE}"]

[train_side_recommendation_kohya]
shuffle_caption = true
keep_tokens = 2                 # "sks" + "1girl/1boy" を固定したい場合
caption_tag_dropout_rate = 0.05
caption_dropout_rate = 0.00
enable_wildcard = true
```

#### SDXLプリセット（balanced hybrid + prompt_2運用前提）

SDXLは2エンコーダ構成で、diffusers側で `prompt_2` を分離できるため、推論時は「タグ（短）＋自然文（短）」に分業させる運用が取りやすいです。citeturn6search3turn6search13turn23search9turn22view0  

```toml
# tagmetry_policy_sdxl.toml（提案フォーマット）

[caption_policy]
name = "sdxl-balanced-hybrid-v1"
trigger_token = "sks"
caption_prefix = "sks, "

[tagging]
engine = "joytag"
threshold = 0.40
max_tags = 45
remove_underscore = false

[hybrid]
mode = "multiline_random"
lines = ["{TAGS}", "{EN_SENTENCE}", "{EN_SENTENCE_DETAILED}"]

[train_side_recommendation_kohya]
shuffle_caption = true
keep_tokens = 1
caption_tag_dropout_rate = 0.10
caption_dropout_rate = 0.00
enable_wildcard = true

[inference_recommendation_sdxl]
# diffusers等での運用イメージ（Tagmetryがメモとして出す想定）
prompt = "sks, {CORE_TAGS}, {TOP_TAGS_SHORT}"
prompt_2 = "sks {EN_SENTENCE_DETAILED}"
negative_prompt = "{COMMON_NEGATIVE_TAGS}"
negative_prompt_2 = "{COMMON_NEGATIVE_SENTENCE}"
```

---

## 具体的なキャプション例とユーザー意思決定ツリー

### 具体例：JoyTag/WD14の“生出力”→学習向け“整形後”→推論プロンプト

#### 例1：キャラクタ（概念）LoRA（アニメ）— Booruタグ中心＋ハイブリッド

**目的**：顔・髪・衣装の同一性は保ちつつ、背景やポーズは変えたい。  
**戦略**：タグ列（可変要素を多め）＋トリガー固定＋タグシャッフル＋軽いタグドロップ。citeturn22view0turn15view1turn28search1turn13view0  

**Before（自動タグ例：JoyTag/WD14系のイメージ）**  
※実出力はモデル・閾値で変わるため、構造例として示します。citeturn13view0turn15view0  

```text
1girl, solo, blue_hair, long_hair, smile, school_uniform, outdoors, tree, sky, looking_at_viewer, ...
```

**After（学習用：固定部＋可変部、英語タグ + 英語短文 + 日本語短文）**  
（トリガー語は常に先頭、1girlも固定部に含める想定）

```text
sks, 1girl || solo, blue hair, long hair, smile, school uniform, outdoors, cherry blossoms, bokeh || a girl with long blue hair in a sailor uniform outdoors
sks 1girl in a sailor uniform with long blue hair outdoors under cherry blossoms
sks、青い長髪のセーラー服の少女、屋外、桜、微笑み
```

- 固定部/可変部/固定サフィックスの分割とシャッフル・タグドロップは、kohya-ss実装が想定する運用に一致します。citeturn22view0turn20search2  
- 「日本語短文」は“ユーザーが日本語でプロンプトしたい”要件に寄せたものですが、SD1.xの学習データは英語中心（LAION-2B(en)）が明示されるため、主戦力は英語側に置くのが安全です。citeturn5search2turn10search37  

**推論プロンプト例（英語/タグ寄り）**（AまたはC向け）

```text
sks, 1girl, solo, blue hair, long hair, school uniform, (smile:1.1), outdoors, cherry blossoms
```

**推論プロンプト例（日本語＋英語ミックス）**（C向け・実験）

```text
sks, 1girl, 青い長髪の少女, sailor uniform, outdoors, cherry blossoms
```

（重み付けや長文処理はUI/ライブラリ依存であるため、diffusersでは prompt_embeds を通じた重み付けが基本になります。citeturn23search9）

#### 例2：画風（スタイル）LoRA — 自然文比率を上げる

**目的**：「水彩風の線・にじみ・紙質感」を、別の題材にも適用したい。  
**戦略**：短い自然文（英語）を主にし、補助的にタグ（質感・技法）を付ける。BLIP系の自動キャプションは便利だが“ノイズが課題”であることが研究としても指摘されるため、短文化・修正が重要です。citeturn7search0turn7search1turn22view0  

**After（学習用：英語短文中心＋補助タグ）**

```text
sks_style, watercolor painting on textured paper, soft edges and pigment bleeding
sks_style, watercolor, paper texture, soft shading, light wash
sks_style、水彩のにじみ、紙の質感、淡い塗り
```

推論側は自然文でのスタイル指定と相性が出やすく、SDXLなら `prompt_2` にスタイル自然文を入れてタグ側と分ける余地があります。citeturn6search13turn6search2turn23search9  

### ユーザー意思決定ツリー（mermaid）

```mermaid
flowchart TD
  A[目的は？] --> B{概念(人物/キャラ/物体)を\n強く固定したい？}
  A --> C{スタイル(画風/質感)を\n広く乗せたい？}

  B -->|Yes| B1{推論時もタグで\n運用できる？}
  B -->|No| B2[自然文キャプション or ハイブリッド\n(可変要素を明示)]

  B1 -->|Yes| T1[Booruタグ列（A）\n+ トリガー固定\n+ shuffle_caption\n+ tag_dropout]
  B1 -->|No| H1[ハイブリッド（C）\nタグ + 短文\n（複数行ランダム）]

  C -->|Yes| C1{ベースはSDXL？}
  C -->|No| C2[タグ中心（A）でも可\nただし背景/構図リーク注意]

  C1 -->|Yes| S1[自然文寄り（B）or バランスC\n推論で prompt_2 を活用]
  C1 -->|No| S2[自然文（B）を検討\nただし短く一貫性を確保]

  T1 --> D{データは少量？}
  H1 --> D
  S1 --> D
  S2 --> D
  B2 --> D
  C2 --> D

  D -->|少量| D1[自動タグ/キャプションを\n必ず手修正\n過学習対策：タグ削減/ドロップ]
  D -->|十分| D2[語彙正規化\nノイズタグ除去\nシャッフル/ドロップ調整]

  D1 --> E[最終：学習設定を回して\nサンプル生成で\n「何が学習されたか」確認]
  D2 --> E
```

（この分岐で鍵になる「トリガー固定」「シャッフル」「タグドロップ」「複数行ランダム」は、kohya-ss実装に具体的な根拠があります。citeturn22view0turn20search2turn27search12）

---

## 参考文献

主要一次資料（公式・論文・実装）  
- entity["organization","CompVis","ml research group"] の Stable Diffusion実装・モデルカード（凍結CLIP ViT-L/14条件付け等）。citeturn5search0turn5search2turn10search9  
- entity["company","Stability AI","ai company"] の SDXL公式リポジトリ／モデルカード（OpenCLIP-ViT/G + CLIP-ViT/L）。citeturn6search0turn6search3  
- entity["company","Hugging Face","ml platform company"] diffusersドキュメント（SDXL API、prompt_2、prompt weighting、LoRA学習スクリプト）。citeturn6search13turn23search9turn8search3turn8search10  
- entity["company","OpenAI","ai research company"] CLIPリポジトリ（context_length=77）。citeturn9search21turn9search25  
- kohya-ss / sd-scripts（train_util のキャプション処理、train_README の identifier 推奨、WD14 tagger）。citeturn22view0turn15view1turn15view0turn20search2  
- 学術論文：LoRA（Hu et al.）、DreamBooth（Ruiz et al.）、BLIP/BLIP-2（Li et al.）、LDM（Rombach et al.）。citeturn9search3turn10search2turn7search0turn7search1turn10search0  

JoyTag／タグデータ  
- JoyTag公式（Danbooruスキーマ、学習データ、性能指標など）。citeturn13view0  
- Danbooruタグ体系・大規模タグデータ（Danbooru2021等）とカテゴリ概念。citeturn12search15turn12search0turn12search7  

コミュニティ実務（経験則として参照）  
- entity["company","Reddit","social platform company"] のLoRAキャプション/タグ運用議論（タグ付け方針、トリガー語、データリーク等）。citeturn3search0turn3search1turn28search1turn28search0  
- entity["company","GitHub","code hosting platform"] issue/discussion（長文プロンプト、prompt weighting、タグツール等）。citeturn23search0turn23search6turn12search7