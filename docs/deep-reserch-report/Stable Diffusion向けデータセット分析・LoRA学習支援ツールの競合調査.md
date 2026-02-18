# Stable Diffusion向けデータセット分析・LoRA学習支援ツールの競合調査

## 調査範囲と前提
本調査は、Stable Diffusion（SD1.5 / SDXL）でのLoRA学習を前提に、「データセットのタグ（booruタグ／カンマ区切りキャプション）を整える・点検する・偏りを把握する・学習を安定させる」ための支援ツール群を対象にしました。たとえば、Kohya系スクリプト群では、学習データ指定方法として DreamBooth（class+identifier／キャプション方式）と、キャプションやタグをメタデータ（JSON）で扱う fine tuning 方式が整理されており、画像が極端に小さい／大きい場合は事前調整が推奨されるなど、実務上の“データ品質”が学習安定性に直結する前提が明示されています。citeturn31view0

また、SDXL向け解像度（例：1024×1024）や、SD1.5向け解像度（例：512系）といった基本的なデータセット・推奨解像度／タグ付け手法（BLIP系＝自然文寄り、DeepBooru系＝booruタグ寄り）、および「自動タグは100%正しくないので編集が必要」という注意は、トレーニング支援系のガイドでも繰り返し強調されています。citeturn8view1

## カテゴリ別の競合マップ
下図は、現場で実際に使われやすい「タグ編集・正規化・QA・学習補助・プロンプト推薦」系ツールを、提供形態（Desktop / Web / Scripts / Plugins）で整理した競合マップです。

| カテゴリ | 代表的な競合（例） | 何が得意か | 代表的な弱点・注意点 |
|---|---|---|---|
| Desktop（ローカルアプリ） | TagGUI（タグ編集＋自動キャプション＋トークンカウンタ）citeturn9view2turn25search4 / BooruDatasetTagManager（booruタグ編集＋翻訳＋補完）citeturn9view3 / sd-tagtool（軽量タグ編集・タグドラッグ等）citeturn21view0 | 手動タグ修正の速度（ショートカット／入力補完）、まとめて置換・削除、タグの視認性 | 大規模フォルダで起動・ロードが遅い、AV誤検知の心理的障壁、OS対応の偏り等citeturn24view0turn23view0turn9view2 |
| Web（ローカルWebUI／オンライン） | dataset-webui（クロップ→ソート→タグ→正規化→出力まで一気通貫）citeturn11view0turn10view0 / （オンライン例）SeaArtのLoRA学習ガイド（データ作り・タグ閾値・トリガー等の指針）citeturn8view1 | ブラウザUIで“工程”を一気通貫化（前処理・分類・タグ剪定）、初心者向けの作法提示 | β品質・既知不具合の明記、あるいはガイドに留まり実装が手元にないciteturn10view0turn11view0turn8view1 |
| Scripts（学習・自動化） | kohya_ss（GUI/CLIでコマンド生成、LoRA/SDXL等対応）citeturn9view0 / sd-scripts（学習・生成・ユーティリティ群、ASL2.0中心）citeturn13search0turn13search28 / OneTrainer（学習＋データセット自動キャプション＋loss算出等）citeturn9view1turn25search2 / wd14-tagger-standalone（CLIタグ付け）citeturn20view0 / wd-llm-caption-cli（WDタグ＋VLMキャプション）citeturn20view1 | 量を捌く、再現性、バッチ処理、学習オプション（shuffle/caption dropout等）の細かな制御 | 設定が複雑（TOML/メタデータ）、環境依存・不具合対応が必要、ログ/指標が理解しづらいciteturn12search5turn13search1turn25search1 |
| Plugins（生成UIへの拡張） | Dataset Tag Editor（A1111向けタグ編集＋多種interrogator）citeturn15view0 / WD14 tagger系（A1111拡張のfork群、ComfyUIノード等）citeturn16view0turn17view0turn20view0 / CLIP Interrogator（画像→プロンプト候補）citeturn19view0turn19view1 / JoyCaption（VLM：SDプロンプト形式など複数モード）citeturn18view0 / ComfyUI-Lora-Auto-Trigger-Words（Civitai APIやメタデータからタグ抽出）citeturn30view0turn30view2 | 生成UIの中で完結（タグ付け→編集→試し打ち）、プロンプト推薦やタグ抽出をワークフローに統合 | UI更新（例：gradio更新）やfork分裂による互換性問題、GPU周りや依存関係で詰まりがちciteturn15view0turn17view0turn2search17turn3search20 |

image_group{"layout":"carousel","aspect_ratio":"16:9","query":["kohya_ss GUI screenshot","stable-diffusion-webui dataset tag editor screenshot","TagGUI Stable Diffusion tags captions app screenshot","BooruDatasetTagManager screenshot","ComfyUI WD14 Tagger node screenshot","CLIP Interrogator Stable Diffusion WebUI extension screenshot"],"num_per_query":1}

## 主要競合プロファイル
ここでは「LoRA学習者が実際に遭遇する“データ品質・安定性”課題」に効く度合いが高いものを、主要競合として整理します（機能・UX・ライセンス/価格・称賛/不満の傾向）。

**kohya_ss（Kohya’s GUI）**  
機能は「幅広い学習パラメータのGUI設定」「CLIコマンド自動生成」「LoRA / DreamBooth / fine-tuning / SDXLを含む複数方式のサポート」を明記しており、支援範囲が“学習実行中心”の統合ツールです。UXはGradioベースで、WindowsだけでなくLinux/macOSもサポートするとしつつ、macOS互換は“環境により差がある”旨も書かれています。ライセンスはApache-2.0です。citeturn9view0  
称賛は「ユーザーフレンドリー」「学習パラメータをGUIで扱える」方向に寄りやすい一方、実運用では自動キャプショニング（WD14等）の不具合や、分岐ブランチ/環境差による“動かない・直す必要”が議論・Issueとして出がちです。citeturn13search1turn13search3

**kohya-ss/sd-scripts（学習・生成・ユーティリティ基盤）**  
リポジトリは「Stable Diffusion等の学習/生成/ユーティリティスクリプト群」であると説明し、ライセンスについて「大部分がASL 2.0（Apache 2.0）で、依存部は別ライセンス」と整理しています。citeturn13search2turn13search0turn13search28  
データ面では、データセット設定（TOML）・サブセット概念・メタデータ（JSON）方式などが体系化される一方、設定の複雑さ（どこに何を書くか、重複時の挙動、メタデータ/latentキャッシュ周りの落とし穴）や、lossの解釈が難しいという“指標問題”がコミュニティ議論として表面化します。citeturn12search5turn12search2turn25search1  
また、caption_dropout_rateのような安定化・汎化狙いオプションもあるものの、効かせ方を誤ると過学習リスクやloss計算の歪みが議論されています（＝“設定がある”だけでは解決しない）。citeturn12search8turn13search36

**entity["organization","OneTrainer","diffusion training ui"]**  
README上で、SD1.5/SDXLを含む多数モデル・形式・学習手法（Full fine-tuning/LoRA/embeddings等）に加え、データセット自動キャプション（BLIP/BLIP2/WD-1.4）、マスク生成、学習中サンプリングUI、TensorBoard連携、さらに「calculate_loss.py（データセット各画像のloss算出）」まで“データQAに近い機能”を明示しています。ライセンスはAGPL-3.0です。citeturn14view0turn9view1turn25search2  
称賛は「一つのUIで学習・サンプル生成・キャプション生成・データ補助が揃う」点に集まりやすい一方、学習の初期段階でノイズっぽい結果が出ることへの不安（＝期待値調整の難しさ）がQ&Aとして出たり、ユーティリティ自体の不具合（calculate_loss.pyのerror issue等）も報告されます。citeturn0search32turn25search6

**TagGUI（デスクトップ：タグ編集＋自動キャプション/タグ生成）**  
GitHub上で「生成AI向け画像データセット作成者のための、クロスプラットフォーム・デスクトップアプリ」とし、キーボード中心の高速タグ付け、頻出タグに基づくオートコンプリート、SD向けトークンカウンタ、自動キャプション/タグ生成、バッチでのリネーム/削除/ソート、フィルタリング等を列挙しています。ライセンスはGPL-3.0で、配布はreleaseからのバイナリ中心、macOSは配布ビルドが無い旨が明記されています。citeturn9view2turn5search8  
ユーザー評価は「キャプション手作業が大幅に楽になった」という称賛が見られる一方、Windows環境でのAV誤検知（Trojan扱い）や、巨大フォルダでの起動遅延（Windows Defenderの再スキャンが原因になり得る、例外設定で改善した等）が不満として出ています。citeturn25search4turn22search0turn24view0  
またIssue一覧として、GPUを使わない・サブフォルダ対応・区切り文字の要望・JoyCaption連携時クラッシュ等、運用上の“詰まりポイント”が継続的に積まれています。citeturn22search1turn25search0

**BooruDatasetTagManager（デスクトップ：booruタグ編集）**  
「hypernetwork/embedding/LoRA等の学習データセット向けのシンプルなタグエディタ」とし、自動タグ付け済みデータの編集、複数画像選択による一括編集、タグ一覧表示、内蔵interrogator RPCでのタグ生成、UIテーマ/ホットキー設定、さらにタグ翻訳（翻訳サービス選択、翻訳キャッシュ保存、手動翻訳優先）などを提供します。ライセンスはMITです。citeturn9view3  
称賛は「自動タグ→手で整える」作業の土台として名前が挙がりやすく、実際にCivitai系の自動タグと組み合わせて使う手順例もコミュニティで語られています。一方で、より高度な検索/ネガティブ条件などの不足が“物足りない点”として挙げられます。citeturn7search8turn0search3

**Dataset Tag Editor（A1111拡張＋スタンドアロン）**  
A1111の拡張として「学習データセットのキャプション編集」を提供し、txtまたはsd-scripts互換のjson（メタデータ）保存、タグ検索、タグでの画像フィルタ（Positive/NegativeでAND/OR対応）、一括置換/削除/追加、タグソート、正規表現置換、BLIP/BLIP2/DeepDanbooru/WD14等の複数interrogatorの利用、独自tagger追加、Aesthetic score predictorの実装など、タグ整備に直結する機能が非常に広いのが特徴です。ライセンスはMITです。citeturn15view0  
不満・課題としては、WebUI側（gradio更新等）との互換性問題や、画像大量時のlag、設定による回避策（テンポラリ保存・最大解像度制限）や“スタンドアロン版推奨”がREADME上で明示されています（＝ユーザーがハマりやすい）。citeturn15view0turn5search13

**WD14 Taggerエコシステム（自動タグ付け：A1111拡張fork／CLI／ComfyUIノード）**  
A1111拡張のfork（例：Akegarasu版）では「DeepDanbooru等を含む複数モデルでbooruタグを単体/複数画像に付与」し、Hugging Faceからモデルを初回実行時に取得する流れが説明されています。citeturn16view0  
ComfyUIノード（MIT）も同様に「booruタグ推定」を提供し、閾値・除外タグなどのパラメータがあり、onnxruntime-gpuは“問題が出やすいのでサポートできない”という注意が書かれています。citeturn17view0  
CLI（corkborg版）でも閾値、CPU/GPU切替、再帰処理、除外/追加タグ、モデル選択などを提供しつつ、著作権/ライセンス表記が“Public domain（借用部除く）”として扱われています。citeturn20view0  
一方でユーザー不満は「fork分裂」「メンテ不在／互換性の破綻」「UI側モジュール変更で壊れる」など“継続運用の不安定さ”に集中しやすく、実際に元リポジトリ削除→fork利用の必要性が言及され、特定WebUI環境での不具合Issueも報告されています。citeturn2search4turn2search17turn3search20

**JoyCaption（VLMキャプショナ：SDプロンプト形式／Danbooruタグ等のモード）**  
JoyCaptionは「拡散モデル学習向けに使える、自由・オープン・（作者表現として）uncensoredな画像キャプションVLM」としてApache-2.0で公開され、Hugging Face DemoやCivitai記事への導線を備えています。SDプロンプト風・Danbooruタグ列・自然文など複数モードがあり、「SDプロンプトモードは約3%でグリッチし得る」「Danbooru/e621等のタグ列モードは精度が低め」といった注意もREADMEに書かれています。VRAMはbfloat16で約17GB必要で、量子化（8bit/4bit）可能という説明です。citeturn18view0  
称賛としては「ComfyUIで使って問題なかった」「デモで試せる」など実務視点の声があり、逆に課題としては（VRAM負荷、統合側でのクラッシュ/設定問題など）統合運用の面倒が挙がりやすいです。citeturn6search6turn25search0

**CLIP Interrogator（画像→プロンプト推薦）**  
「既存画像に近い画像を作るための良いプロンプトを推定する」目的で、entity["company","OpenAI","ai research org"]のCLIPとentity["company","Salesforce","ai research"]のBLIPを組み合わせる“プロンプトエンジニアリングツール”と説明されています。MITで、CLI/Gradio、Colab、entity["company","Hugging Face","ml platform"]、entity["company","Replicate","ml hosting platform"]等での提供形態が明記され、低VRAM設定の目安（既定約6.3GB→低VRAM約2.7GB）も書かれています。citeturn19view0  
A1111拡張もMITで、API（/interrogator/models, /prompt, /analyze）を提供するなど、UI統合に強い一方、研究面では「生成された文章が不完全で十分な情報を提供できず満足いく結果に繋がらないことがある」という指摘があり、“プロンプト推薦＝最終回答”になりにくい性質が示唆されます。citeturn19view1turn4search6

**Civitai系（Web：クラウド学習＋自動タグ付けの存在）**  
LoRA学習者の文脈では、entity["company","Civitai","ai model sharing platform"]の“オンサイトAuto-tagger”が言及され、第三者タグ付けと比較して「オンサイトtaggerでも十分」「ただし最終的に追加/削除の手直しはする」といった運用が語られます。citeturn27search0turn28search4  
一方で「衣装など固有概念はskirt/shirtに潰れる」「特徴を落として省略される」など、自動タグの限界が実例として提示され、手動補正の必要が浮き彫りになります。citeturn28search1  
補助的に、ComfyUI側ではCivitai APIやモデル埋め込みメタデータから“LoRAのトリガー語/タグ”を抽出し、頻度順に整形・選別・重み付けするノードが存在し、プロンプト推薦（トリガー語再利用）をワークフローに組み込めます。citeturn30view0turn30view2

## LoRA学習者が繰り返し言及する未充足ニーズ・痛点
複数コミュニティ（entity["organization","Reddit","social news site"]投稿、entity["organization","GitHub","code hosting platform"] Issue/Discussion、ガイド類）から、データ品質と学習安定性に関する“繰り返し出る痛点”を5つに要約します。

第一に、「自動タグ／自動キャプションは必ず間違うので、結局“直す工程”がボトルネック」問題です。ガイド側でも「自動タグは100%正しくないので編集が必要」と明記され、実例として衣装固有名詞などは一般語（skirt/shirt/dress）に潰れたり、そもそも欠落することが起きています。citeturn8view1turn28search1

第二に、「一括編集はできても、“正規化（同義語統一・表記ゆれ・ブラック/ホワイトリスト・置換ルール）”と“その影響確認”が弱い」問題です。dataset-webuiは“Normalize tags / blacklist/whitelist / replace tags / ruleを編集して効果をテスト”まで踏み込んでいますが、これは例外的で、他ツールは検索・置換・削除中心になりがちです。citeturn11view0turn15view0

第三に、「タグ分布（偏り）と、学習結果の症状（過学習/下振れ/バイアス）を結び付けて診断できない」問題です。ユーザーは“過学習と下振れの両方が出る”ような最適化迷子になり、また“表情や服装の偏りをタグで分離していなかったせいで、モデルが怒って裸ばかりになる”といったデータ起因のバイアスを後から発見します。citeturn7search6turn28search4  
一方で、loss自体の解釈が難しい（lossはタイムステップ等のバイアスを受ける可能性があり、データごとのloss曲線を前計算して解釈しようという議論が出る）など、指標が診断に直結しにくい課題もあります。citeturn25search1turn7search1

第四に、「ツールチェーンが分裂していて、互換性（WebUI更新・fork分裂・依存関係）で壊れやすい」問題です。Dataset Tag Editorはgradio更新などで“古いWebUIをサポートしない/互換性確認が必要”と書き、WD14 tagger系は元repo削除→fork依存の状況が続き、環境によってはモジュール欠落で動かないIssueも出ています。citeturn15view0turn2search4turn3search20

第五に、「“学習安定化オプション”は多いが、何をどう選べば良いかが分かりにくい」問題です。sd-scriptsにはcaption dropoutやshuffle等があるものの、効果はデータセット/目的に依存し、やり過ぎはリスク（過学習誘発やloss計算の歪み）と議論されます。またタグ順序が学習に影響するためshuffle_captionが推奨されるなど、知識は散在しています。citeturn12search8turn13search36turn12search20

## Tagmetry向けV1提案と差別化
ここからは、上記の競合状況と痛点（特に“直す工程”“偏り診断”“壊れにくさ”）を踏まえ、月額$3–$7級で「プレミアムに感じる最小V1」と、Tagmetryが勝ちやすい差別化を提案します。提案は“製品仮説”であり、以下の機能自体は競合ソースの事実主張ではありません（痛点の根拠は前節ソース）。citeturn8view1turn15view0turn11view0turn7search6turn2search4

V1の最小機能セットは、「タグ品質を“数値化して直しやすくする”」に集中させるのが成立しやすいです。具体的には、(a) データセット取り込み（フォルダ、txt/caption、sd-scripts JSONメタデータ、A1111拡張の形式などを吸収）、(b) タグ正規化パイプライン（表記ゆれ・同義語統一・不要タグ一括除去・ブラック/ホワイトリスト・ルールエンジン）、(c) タグ統計（タグ頻度、共起、1枚あたりタグ数分布、希少タグ/過多タグ、トリガー語の混入・欠落検知）、(d) QAチェック（極端に小さい/大きい画像・アスペクト比、重複疑い、キャプション欠落、タグ過剰/不足の外れ値、画像ごとの“要確認キュー”）、(e) 修正提案（例：このデータセットは背景タグが一方向に偏っている、衣装タグが一般語に潰れている等）を“理由つき”で提示、(f) エクスポート（kohya/OneTrainerで再学習しやすい形）を、1つのローカルアプリ（またはローカルWeb）で完結させます。これは、現状の「自動タグ→編集→別ツールで分析→学習→失敗→戻る」往復コストを縮める狙いです。citeturn10view0turn11view0turn15view0turn7search6

価格帯$3–$7で“プレミアム感”を出すには、機能数よりも「壊れにくさ」「説明責任（なぜそう言うか）」「多言語・多文化対応」が効きます。たとえば、TagGUIが直面した“AV誤検知”や巨大フォルダでの体感問題は、配布・起動体験の不信に直結します。V1では署名・配布形態、初回インデックスの見える化（何をスキャンしているか）、巨大フォルダ時の段階ロード、失敗時の復旧（“固まった状態を解除できる”）を優先し、安心して継続利用できる体験を作るのが合理的です。citeturn24view0turn22search0turn25search29

Tagmetryの“キラーディファレンシエータ”を3つ挙げるなら、次が筋が良いです。第一に「タグ正規化の“ルール→影響プレビュー→安全な一括適用→ロールバック”を、非エンジニアでも扱える形で提供」することです。dataset-webuiが一歩近いものを持ちますが、β品質・既知不具合も明記されており、ここを“商用品質”で安定提供できる余地があります。citeturn11view0turn10view0  
第二に「“偏りの原因”を、人間が意思決定できる粒度で説明する診断」です。たとえば“怒って裸ばかり”のような症状は、タグで表現差分を分離していなかった（偏りを明示していなかった）ことが原因になり得ますが、現状はユーザーが後から気づきます。Tagmetryはタグ分布と症状を結び付けた“説明テンプレ”を持つべきです。citeturn28search4turn7search6  
第三に「SD1.5（booruタグ）とSDXL（自然文寄りキャプション）を“同一データセットの別ビュー”として扱い、目的別に最適化案を切り替える」ことです。ガイド側でもBLIP系とDeepBooru系の推奨使い分けが存在し、ツール側もWD14やBLIP2を併用しているため、複数表現を統合して管理する価値があります。citeturn8view1turn9view1turn15view0turn20view1

## ポジショニングとタグライン案
**Positioning statement（英語、1–2文）**  
Tagmetry is a premium dataset QA and tag-normalization companion for Stable Diffusion LoRA training, turning messy auto-captions into consistent, balanced training data with clear, explainable fixes. It helps creators ship better LoRAs faster by diagnosing dataset bias and reducing training instability at the source.citeturn8view1turn11view0turn7search6turn25search1  

**Tagline ideas（英語、5案）**  
Make your tags train-ready.citeturn8view1turn15view0  
From noisy captions to clean LoRAs.citeturn8view1turn28search1  
Fix the dataset, fix the model.citeturn7search6turn25search1  
Train stable. Tag smart.citeturn12search8turn13search36  
Dataset QA you can trust.citeturn10view0turn15view0