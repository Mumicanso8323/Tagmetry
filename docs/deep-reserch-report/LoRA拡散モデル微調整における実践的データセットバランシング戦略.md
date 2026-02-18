# LoRA拡散モデル微調整における実践的データセットバランシング戦略

## エグゼクティブサマリー

本レポートは、拡散モデル（特にLoRAによる微調整）において「**ベースモデルを再学習せず**に、データセット／データローダ（サンプラ）側だけで適用できるバランシング戦略」を、実務者コミュニティと一次文献（論文・公式ドキュメント・GitHub Issues/Discussions）を軸に体系化した。LoRAは「事前学習済み重みを凍結し、低ランク更新を注入する」方式として提案され、フル微調整より学習対象パラメータを大幅に減らすため、**“データの見せ方”が性能・過学習・プロンプタビリティに直結しやすい**。citeturn12search2turn12search10

拡散モデルの個別概念学習（DreamBooth系）では、小規模データにより「**language drift（クラス語が特定個体に引っ張られる）**」が起き得るため、クラス一般性を保つための **prior preservation（正則化／クラス画像混合）** が提案され、公式実装でもクラス画像・prior loss重みが提供されている。citeturn22view1turn1view1turn6view1 一方、コミュニティ実装では「フォルダ別repeat（num_repeats）」「部分サンプリング（multiplier）」「caption/tag dropout・shuffle caption」「フォルダ別loss scale」など、**ローダレベル調整で“概念間の露出回数”を整える**手法が広く使われている。citeturn2view0turn17view1turn8view0turn11view2turn14view4

重要な結論は次の通り。

- **最優先で安全な手段は「弱いランダム化」**（shuffle caption、軽いcaption/dropout、軽い画像Aug）であり、データ分布を大きく歪めずに「特定タグへの依存」「語順バイアス」「少数ショットの固定化」を緩和しやすい。citeturn25view0turn2view0turn24view1turn1view3  
- **強いバランシング（過度なオーバーサンプル／重み付け）は“記憶・滲み（bleeding）”を誘発**しやすい。EveryDream系ドキュメントは、強いハンマリングで「顔の破綻やスタイルの滲み」が出る例を挙げ、対策として“preservationデータ混合”や倍率制御を推奨している。citeturn8view0  
- **コンシューマ向け自動化（Tagmetry想定）**では、(a) データが小さい場合は「原則バランスしない」(b) マルチ概念で枚数差が大きい場合のみ「フォルダ／カテゴリ単位の露出回数合わせ」を行い、(c) タグ分布は「cap（上限制御）より“タグdropout＋保持トークン”」をデフォルトにするのが事故率が低い。citeturn8view0turn25view0turn24view1turn20view1  

## 問題設定と前提

LoRA微調整では、巨大なベースモデル全体を再学習するのではなく、追加パラメータ（低ランク行列）だけを学習する設計が一般的であるため、**同一データを何回見せるか／どのデータをどの比率で混ぜるか**が、（1）概念の定着速度、（2）プロンプトへの反応性、（3）過学習（記憶・滲み）を強く左右する。citeturn12search2turn8view0

また拡散モデルの個別概念学習は、分類の「クラス不均衡」と似ているが、目的が「クラス境界」ではなく「条件付き生成の分布近似」であるため、単純な均等化が必ずしも良いとは限らない。DreamBoothは、少数画像によりクラス語が個体に結びつく drift を抑えるため、**class-specific prior preservation loss** を導入し、クラス一般性を維持しながら個体を埋め込むことを狙う。citeturn22view1 公式トレーニングガイドでも、モデルが生成したクラス画像を使う prior preservation loss と、その重み（prior_loss_weight）やクラス画像ディレクトリがオプションとして提供されている。citeturn1view1

実務では、学習スクリプト（例：entity["company","Hugging Face","ml platform company"]のdiffusers、entity["organization","PyTorch","deep learning framework"]）や、entity["organization","kohya-ss","sd-scripts maintainer"]系の学習基盤が提供する「repeat」「サブセット」「captionシャッフル／dropout」「画像Aug」を組み合わせ、**“ベースを惰性で壊さずに” 目的概念の露出を増やす**設計が多い。citeturn1view1turn2view0turn17view1turn14view4

image_group{"layout":"carousel","aspect_ratio":"16:9","query":["DreamBooth prior preservation loss diagram","oversampling undersampling class imbalance diagram","stratified sampling diagram dataset loader","LoRA adapter diffusion fine tuning diagram"] ,"num_per_query":1}

## データセット／ローダレベルでのバランシング手法カタログ

ここでは、要求された各手法を「定義」「実装メモ（擬似コード）」「計算コスト」「適用条件」で整理する。併せて、拡散LoRA界隈で実際に使われている設定項目（例：`num_repeats`、`shuffle_caption`、`caption_dropout_rate`、`caption_tag_dropout_rate`）との対応も示す。citeturn2view0turn17view1turn25view0turn14view4

### 技法比較テーブル

評価軸の意味（本レポートの用語）  
- **Bias risk**: 生成の“素の分布”（ベースモデルの事前分布＋元データ分布）を、意図せず別分布へ押し曲げるリスク。  
- **Variance impact**: 勾配推定のばらつき（サンプル多様性の減少／サンプリングの確率化で変動が増える等）。  
- **Overfitting risk**: 記憶・滲み・プロンプト依存性崩壊など、“少数データを叩きすぎ”による崩れ。  
- **Ease**: 既存トレーナ（sd-scripts / diffusers / PyTorch）に後付けしやすいか。

| 手法 | Bias risk | Variance impact | Overfitting risk | Ease | 代表ハイパラ例 |
|---|---|---|---|---|---|
| オーバーサンプリング（repeats） | 中〜高 | 低下（多様性↓） | 高 | 易 | `num_repeats`、target exposure/epoch |
| アンダーサンプリング（部分epoch） | 中 | 上昇（毎epochの揺れ↑） | 中（偏り次第） | 中 | fraction（例0.5）、seed固定 |
| per-tag cap（タグ上限制御） | 中 | 中 | 低〜中 | 中 | cap_ratio（例0.8）、除外対象タグ |
| stratified sampling（カテゴリ層化） | 低〜中 | 中 | 中 | 中〜難 | カテゴリ定義、各カテゴリ比率 |
| per-image weights（重み付き抽出／loss weight） | 中〜高 | 中（重み次第） | 中〜高 | 中 | weight smoothing、clip、温度 |
| curriculum-style sampling（段階制御） | 低〜中 | 中 | 低〜中 | 中〜難 | スケジュールτ、warmup step |
| ハイブリッド＋Aug（dropout/shuffle/正則化混合） | 低〜中 | 中 | 低〜中 | 易〜中 | `shuffle_caption`、dropout率、正則化比 |

（ハイパラ名のうち `num_repeats` / `shuffle_caption` / `caption_dropout_rate` / `caption_tag_dropout_rate` 等は、sd-scripts側でサブセット単位に指定できる。citeturn2view0turn17view1 `WeightedRandomSampler` はPyTorch標準の重み付き抽出として利用できる。citeturn14view4）

### オーバーサンプリング（over-sampling / repeats）

**定義**  
少数概念・少数タグを含む画像（またはサブセット）を、1エポック内で複数回見せて有効サンプル数を増やす。sd-scriptsでは `num_repeats` がサブセット単位で指定でき、fine-tuningの `--dataset_repeats` 相当として説明される。citeturn2view0turn17view1 diffusersのDreamBooth LoRA系スクリプトでも `--repeats`（「訓練データを何回繰り返すか」）が引数として存在する。citeturn3view0

**実装メモ（擬似コード）**  
もっとも単純には「インデックスの複製」を作る。

```python
# repeat-based oversampling (deterministic)
def build_repeated_indices(indices, repeats: int):
    out = []
    for idx in indices:
        out.extend([idx] * repeats)
    return out

# usage:
# train_indices = build_repeated_indices(range(N), repeats=3)
# DataLoader(dataset, sampler=SubsetRandomSampler(train_indices), ...)
```

トレーナ実装としては「サブセットごとに `num_repeats` を掛けた実効枚数をエポック長に反映」する設計が多い。citeturn2view0turn17view1turn8view0

**計算コスト**  
- 前処理：O(N)（インデックス複製するとメモリ増）  
- 学習：実効ステップ数が repeats 倍に増える（計算時間が素直に増える）

**適用条件**  
- マルチ概念（人物A/B、スタイルA/Bなど）で枚数差が大きいとき。citeturn8view0turn11view2  
- “強く覚えさせたい”少数概念が明確で、かつ後述の正則化（クラス画像・preservation混合）や早期停止を併用できるとき。citeturn6view1turn22view1  

### アンダーサンプリング（under-sampling / 部分epochサンプリング）

**定義**  
多数派（枚数が多い概念）を毎エポックすべて使わず、ランダムに一部だけ使う（または無作為に間引く）ことで、概念間の露出回数を近づける。

拡散LoRA界隈では「repeatで増やす」よりも、「**multiplierで“各エポックで使う割合”を指定**」という要望として明確に議論されている。GitHub Discussionでは「0.5 multiplierなら各エポックで半分の画像をランダム選択」「1.6も可」「負値で除外」等の仕様案が提示された。citeturn11view2 EveryDream2trainerの `multiply.txt` はまさに同型で、小数（例0.4）を指定すると“残り部分が毎エポックランダム選択”になると説明している。citeturn8view0

**実装メモ（擬似コード）**  
「各エポックで subset_size を決めてサブサンプリング」を行う。

```python
import random

class FractionalEpochSampler:
    def __init__(self, indices, fraction: float, seed: int = 0):
        self.indices = list(indices)
        self.fraction = fraction
        self.seed = seed
        self.epoch = 0

    def set_epoch(self, epoch: int):
        self.epoch = epoch

    def __iter__(self):
        rng = random.Random(self.seed + self.epoch)
        k = max(1, int(len(self.indices) * self.fraction))
        batch = rng.sample(self.indices, k=k)  # without replacement
        return iter(batch)

    def __len__(self):
        return max(1, int(len(self.indices) * self.fraction))
```

**計算コスト**  
- 前処理：O(N)  
- 各エポック：サンプリングに O(k)  
- 学習：実効ステップ数は fraction 倍に減る（速いが、毎epochのデータが揺れる）

**適用条件**  
- 大量画像の概念を“全部使う必要がない”場合（例：スタイルAは1000枚あるが、他概念とのバランスが重要）。citeturn1view4turn11view2turn8view0  
- データが大きく、毎エポック全走査が無駄になりやすい場合（ただしLoRA界隈ではGPU計算が支配的で、ローダ最適化より「学習ステップ総量」のほうが効くことが多い点に注意）。citeturn8view0turn14view4  

### per-tag cap（タグ露出の上限制御）

**定義**  
タグ（例：booru tag、属性語）について、学習中に“出現頻度が高すぎるタグ”が支配するのを防ぐため、(a) そのタグを含むサンプルの選択確率を下げる、または (b) キャプション側からタグ自体を一部の画像で削る（=見せない）ことで、タグ露出を上限化する。

現場的には「capのための専用サンプラ」より、**タグdropout／キャプションdropout** を使って「特定語に依存しすぎない」状態を作るアプローチが多い。caption dropoutは「キャプション全体を一定割合で空にして学習する」ものとして説明され、0は常にキャプション使用、1は常にキャプション無し、という連続パラメータである。citeturn24view0turn24view1 また community issue では「caption tag dropout はカンマ区切りタグが確率で落ちる」と整理されている。citeturn20view1

**実装メモ（擬似コード：タグcapを“キャプション編集”で実現）**  
「頻出タグほど高い確率で落とす」= 期待値としてcapする。

```python
from collections import Counter
import random

def compute_tag_freq(captions):
    # captions: list[list[str]]  (already split by comma etc.)
    c = Counter()
    for tags in captions:
        c.update(set(tags))  # presence-based freq
    return c

def apply_tag_caps(captions, cap_ratio=0.8, keep=set(), seed=0):
    """
    For tags appearing too frequently, probabilistically drop them so that
    expected presence <= cap_ratio * N.
    """
    rng = random.Random(seed)
    N = len(captions)
    freq = compute_tag_freq(captions)

    out = []
    for tags in captions:
        new_tags = []
        for t in tags:
            if t in keep:
                new_tags.append(t)
                continue
            f = freq[t]
            cap = cap_ratio * N
            if f <= cap:
                new_tags.append(t)
            else:
                # drop with probability proportional to excess
                drop_p = 1.0 - (cap / f)
                if rng.random() >= drop_p:
                    new_tags.append(t)
        out.append(new_tags)
    return out
```

**計算コスト**  
- 前処理：O(total_tags)（タグ頻度計算）  
- 学習時：キャプション生成が動的な場合、テキスト埋め込みキャッシュが難しくなることがある（後述）。citeturn9view1  

**適用条件**  
- “品質用タグ”や“常に付くタグ”が多く、生成時にそれを入れないと出ない（＝語への依存）が問題になっている場合。実際に、部分的caption dropout（部分トークン削除）を求める理由として「全部のトークンを入れないとサブパーになる」問題が挙げられている。citeturn20view0  
- タグ体系が比較的安定（誤タグが少ない）な場合。誤タグが多いと、capが「正しいサンプル」を減らしてしまう。

### stratified sampling（カテゴリ層化サンプリング）

**定義**  
データをカテゴリ（例：概念ID、人物、スタイル、画角、背景タイプ）で分割し、各カテゴリを指定比率で混ぜて学習する方式。sd-scriptsは「複数データセット／複数サブセット」を設定でき、サブセットは画像ディレクトリやメタデータで分割されると説明されている。citeturn2view0turn17view0 この“サブセット”をカテゴリとして扱い、層化抽出を実装するのが実務的に最短である。

**実装メモ（擬似コード：カテゴリ別に均等に引く）**

```python
import random
from collections import defaultdict

class StratifiedSampler:
    """
    Each epoch, sample k_g items from each group g, then shuffle globally.
    """
    def __init__(self, group_to_indices, k_per_group, seed=0):
        self.group_to_indices = {g: list(v) for g, v in group_to_indices.items()}
        self.k_per_group = dict(k_per_group)
        self.seed = seed
        self.epoch = 0

    def set_epoch(self, epoch):
        self.epoch = epoch

    def __iter__(self):
        rng = random.Random(self.seed + self.epoch)
        out = []
        for g, idxs in self.group_to_indices.items():
            k = min(self.k_per_group.get(g, 0), len(idxs))
            if k <= 0:
                continue
            out.extend(rng.sample(idxs, k=k))
        rng.shuffle(out)
        return iter(out)

    def __len__(self):
        return sum(min(self.k_per_group.get(g, 0), len(v)) for g, v in self.group_to_indices.items())
```

**計算コスト**  
- 前処理：O(N)でgroup分割  
- 各エポック：O(Σk_g)  
- 学習：k_g設計でステップ数を制御可能

**適用条件**  
- “何をバランスさせたいか”がカテゴリで定義できる場合（例：フォルダ=概念、メタデータ=タグ集合）。  
- マルチ概念LoRAで、概念間の干渉を減らしたい場合（後述の失敗モード参照）。citeturn15view2turn17view1  

### per-image weights（画像単位の重み付け：抽出確率 or loss weight）

**定義**  
画像ごとに重みを持たせ、(a) その重みに比例して抽出する（確率的バランシング）、または (b) その画像の損失を重み付けする（学習ステップは増やさず影響度だけ変える）。

(a) はPyTorchの `WeightedRandomSampler` で「与えたweightsに基づきインデックスをサンプルする」形で標準化されている。citeturn14view4  
(b) は拡散LoRA界隈でもフォルダ単位 `loss_scale.txt` として実装されており、0.5なら「学習ステップサイズを半分にする」効果として説明され、負の値は危険だと警告されている。citeturn8view0

**実装メモ（PyTorch WeightedRandomSampler例）**

```python
import torch
from torch.utils.data import DataLoader, WeightedRandomSampler

# weights: length N, one weight per image
weights = torch.tensor(per_image_weights, dtype=torch.double)

sampler = WeightedRandomSampler(
    weights=weights,
    num_samples=len(weights),   # epoch length
    replacement=True            # sampling with replacement
)

loader = DataLoader(dataset, batch_size=B, sampler=sampler, num_workers=..., pin_memory=True)
```

（`WeightedRandomSampler` の仕様：weightsは正規化不要、replacementの有無、例が公式ドキュメントに示されている。citeturn14view4）

**実装メモ（loss weight例：ミニバッチ損失に掛ける）**

```python
# in training loop:
loss = mse(pred, target)            # shape [B] or scalar
loss = (loss * sample_weight).mean()
loss.backward()
```

sd-scripts側にも、デバッグ出力で `loss_weights` をサンプルごとに持つ設計が見えるため、実装としては自然に載る。citeturn9view2

**計算コスト**  
- 抽出重み：前処理 O(N)  
- loss重み：ほぼゼロオーバーヘッド  
- 注意：重みが極端だと勾配が不安定になりやすい（後述）

**適用条件**  
- 「画像を増やしたくないが、特定画像の影響度だけ上げたい」場合（loss weightが有効）。citeturn8view0  
- ノイズの多いデータを弱めたい／品質の高いデータを強めたい（ただし重み付けは“暗黙に分布を変える”ので慎重に）。

### curriculum-style sampling（カリキュラム／段階的サンプリング）

**定義**  
学習初期は“易しい・安定する”分布（例：高品質・中庸なデータ、短いキャプション、均等カテゴリ）に寄せ、学習が進むにつれて元の分布へ近づける（あるいは逆）という段階設計。一般のカリキュラム学習は、難易度を上げる提示順序が汎化に寄与し得るとして古典的に定式化されている。citeturn23search8

拡散LoRA界隈では、sd-scriptsのサブセット設定に `token_warmup_min` / `token_warmup_step` があり、「step=0におけるタグ数」「Nステップ目でタグ数が最大になる」とコメントされているため、**キャプション内タグ数を段階的に増やす**設計が実装レベルで示唆される。citeturn23search22

**実装メモ（擬似コード：balanced→originalへの混合比を時間で変化）**

```python
# p_balanced decreases over time (start balanced, end original)
def p_balanced(step, tau):
    import math
    return math.exp(-step / tau)

# sample strategy:
# with probability p_balanced(step), draw from balanced sampler
# else draw from original/uniform sampler
```

**計算コスト**  
- サンプラ2系統の用意（balanced / original）  
- 追加オーバーヘッドは軽いが、設計・デバッグ工数が増える

**適用条件**  
- マルチ概念で相互干渉が出るが、最終的には自然な分布も維持したい場合。  
- タグが長く、いきなり全部条件付けすると「特定語への依存」が起きやすい場合（段階的に条件を増やす狙い）。citeturn20view0turn23search22  

### ハイブリッド／Augベース（キャプション操作・画像Aug・正則化データ混合）

**定義**  
サンプル比率を直接いじるだけでなく、**“同じ画像でも条件（キャプション）を揺らす”**、**“微小画像変形で入力を揺らす”**、**“別データ（正則化／preservation）を混ぜる”**で、実効的なバランスと汎化を狙う。

代表例：
- **shuffle caption**：カンマ区切り語順を毎回変える。語順バイアス（先頭語が重い）を緩和する狙いが説明されている。citeturn25view0turn6view1  
- **keep tokens**：先頭の一定語を固定し、トリガ語を守る。citeturn25view0turn2view0  
- **caption dropout**：一定割合でキャプション無し学習を混ぜる。「特定語に特徴を結び付けすぎない」期待が説明される一方、やりすぎると“プロンプト無しLoRA”になり得ると警告されている。citeturn24view1turn24view0  
- **caption tag dropout**：タグ単位で確率的に削る（GUIへの要望としても定義が議論される）。citeturn20view1turn2view0  
- **画像Aug（色・反転・ランダムクロップ等）**：学習用画像を軽く変形し種類を増やす。Color augmentation / Flip augmentation の効果・制約が説明されている。citeturn25view2turn25view3turn2view0  
- **正則化（クラス画像／preservation混合）**：sd-scripts日本語ドキュメントは、正則化画像が「class全体が学習対象に引っ張られる（language drift）ことを防ぐ」と説明し、繰り返し回数は“学習用と正則化の枚数を合わせて1:1で学習するため”に使う、としている。citeturn6view1turn22view1  
- **preservationデータの弱混合**：EveryDream2trainerは、新規概念200枚＋preservation1000枚の例で、preservation側に 0.05〜0.1 の `multiply.txt` を置き、毎epoch 50〜100枚だけランダムに混ぜれば十分だと提案している。citeturn8view0  

**実装メモ（キャプションshuffle＋tag dropoutの簡易例）**

```python
import random

def shuffle_and_tag_dropout(tags, keep_first_k=1, tag_dropout=0.1, seed=None):
    rng = random.Random(seed)
    fixed = tags[:keep_first_k]
    rest = tags[keep_first_k:]
    rng.shuffle(rest)

    kept = []
    for t in rest:
        if rng.random() >= tag_dropout:
            kept.append(t)
    return fixed + kept
```

**計算コスト（重要）**  
キャプションが動的に変化する（shuffle/dropout/token warmup）と、テキストエンコーダ出力のキャッシュが難しくなる場合がある。sd-scriptsの実装では、`caption_dropout_rate`・`shuffle_caption`・`token_warmup_step`・`caption_tag_dropout_rate` 等が有効だと「テキストエンコーダ出力キャッシュ不可」判定になる。citeturn9view1turn23search22  
これは「バランシングのためのランダム化」と「高速化（キャッシュ）」のトレードオフであり、Tagmetryの既定値設計でも考慮が必要になる。

**適用条件**  
- 少数データでの“記憶”を避けたいとき（Augやdropoutは比較的安全）。citeturn24view1turn25view2  
- 正則化画像やpreservation混合を入れられるとき（強いrepeatsの副作用を抑える）。citeturn6view1turn8view0turn1view1  

## 手法別の失敗モードと実務上の注意

ここでは、各手法の「利点／欠点」と、拡散LoRA文脈で実際に報告されている“壊れ方”を具体化する。なお、失敗モードは単独手法ではなく「データ品質」「キャプション設計」「学習率・ステップ」「正則化有無」に依存するため、**“起きやすい形”**として読むのが安全である。citeturn8view0turn24view1turn22view1  

### オーバーサンプリングの失敗モード

利点は「少数概念が学習に埋もれない」点だが、欠点は「多様性が増えないまま同じ画像を叩く」ことである。特に次が起きやすい。

- **過学習（見た目の固定化・焼き付き）**：EveryDream2trainerは、強いハンマリングで「sunburnt looking faces」「malformed faces」「style bleeds」などのアーティファクト例を挙げ、preservation混合で“忘却”を防ぐべきだと述べる。citeturn8view0  
- **language drift（クラス語の汚染）**：sd-scripts日本語ドキュメントは、正則化無しで `shs 1girl` 的に特定キャラを学習すると、単に `1girl` でもそのキャラに寄ってしまう現象を説明している。citeturn6view1 DreamBooth論文も drift を明示し、回避のため prior preservation loss を導入する。citeturn22view1  

実務対策は「repeatsを上げるほど正則化／preservation混合を強める」「早期停止（中間サンプル確認）」「captionのランダム化（shuffle/dropout）で条件依存を弱める」が基本になる。citeturn6view1turn25view0turn24view1

### アンダーサンプリングの失敗モード

利点は「多数派に学習を支配させない」「ステップ数短縮」だが、欠点は「毎epochの見え方が揺れ、重要パターンを取り逃し得る」点。

- **カバレッジ不足**：fractionが小さすぎると、多数派概念の“代表性”が落ち、結果として生成の幅や背景多様性が落ちる。これは特にスタイルLoRAで顕在化しやすい（背景・筆致・色域の学習が不十分）。citeturn8view0turn25view2  
- **エポック間のブレ**：EveryDream2trainerは小数倍率（例2.5の余り0.5）が「毎epochランダム選択」になると説明しており、同様に部分サンプリングは学習が揺れやすい。citeturn8view0  

対策は「seed固定」「fraction下限（例0.3〜0.5）」「エポックではなく総ステップで管理」「カテゴリ内での均等化（層化）」である。citeturn8view0turn14view4

### per-tag cap／caption/tag dropoutの失敗モード

利点は「特定語への依存を減らして短いプロンプトでも出やすくする」方向性だが、やりすぎると次の壊れ方がある。

- **“プロンプト無しLoRA化”**：kohya_ss wikiは、caption dropout（キャプション無し学習）は「特定語に結び付けすぎない」効果が期待できる一方で、「使いすぎると“prompts無しLoRA”になり得るので注意」と明示する。citeturn24view1turn24view0  
- **過学習を逆に誘発するケース**：sd-scripts discussionでは、`caption_dropout_rate` について「少しは柔軟性に効くが、多すぎると過学習リスクが上がる」と述べ、「caption_tag_dropout_rate と shuffle_captions の方がバランスが良い」との経験談がある（短い引用）。citeturn1view3  
- **概念分離の失敗**：captionless比率が高すぎると、マルチ概念（複数トリガ語）で「何を出すべきか」が曖昧になり、概念が混ざることがある。実際にcaptionless学習の利点（短いプロンプトで似せやすい）と欠点（概念の細分化が難しい）が比較され、「1:1混合は高すぎて主特徴の overfix を示す」と報告されている。citeturn20view2  

実務対策は「keep_tokens（トリガ語固定）＋tag dropoutは低率（0.05〜0.15）から」「caption dropoutはさらに低率から」「スタイル目的だけ上げる（後述推奨値）」である。citeturn25view0turn24view1turn23search11  

### per-image weights／loss scaleの失敗モード

利点は「ステップ数を増やさず影響度を調整できる」ことだが、重みの極端さが問題になる。

- **勾配の暴れ／破綻**：loss scale を負値にすると「モデルを本当に壊し得るので注意」と明示されている。citeturn8view0  
- **意図しない分布歪み**：重み付けは確率分布を変えるため、結果的に“ベースの一般性”を削り、滲みを増やすことがある（特に重みを上げたデータが狭いスタイルに偏る場合）。citeturn8view0turn22view1  

対策は「重みのクリップ（例：0.5〜2.0）」「重みのスムージング」「重みを上げるより下げる（ノイズを弱める）」が無難。EveryDreamの `multiply.txt` も、バランス調整は 1〜3程度を推奨している。citeturn8view0  

### 正則化（prior preservation／preservation混合）固有の失敗モード

- **生成クラス画像の誤差を学習してしまう**：EveryDream2trainerはDreamBooth系が「生成画像を正則化に使う」ことについて、モデルの既存誤り（例：余分な指）を強化し得る、と批判し、“ground truthの混合”を推奨している。citeturn8view0  
- **正則化データ品質の影響**：sd-scripts日本語ドキュメントも、正則化画像の品質がモデルに影響すると注意している。citeturn6view1  
- **比率設計の失敗**：正則化が少なすぎると drift を抑えられず、多すぎると目的概念が弱くなる。sd-scriptsは repeats を「学習用画像の繰り返し回数×枚数≧正則化側の同値」になるように、と比率設計の目安を提示している。citeturn6view1  

## Tagmetry向け推奨設定と意思決定フロー

ここでは、一般ユーザ向けツール（Tagmetry）として「事故りにくいデフォルト」「閾値」「バランスしない判断」「安全なフォールバック」を提案する。なお、一部はコミュニティ推奨値（例：multiply.txt 1〜3、preservation 0.05〜0.1、caption tag dropout 0.1）を根拠にしている。citeturn8view0turn23search11turn25view0turn24view1  

### デフォルト方針

Tagmetryの既定値は、次の優先順位が安全。

1) **分布を大きく変える“強いバランス”はデフォルトOFF**（repeatsの自動増減、重みの大調整はユーザが明示的にON）。  
2) デフォルトONは **“弱いランダム化”**：  
   - `shuffle_caption`: ON（タグ列の語順バイアスを緩和）。citeturn25view0turn6view1  
   - `keep_tokens`: 1（トリガ語・識別子を固定）。citeturn25view0turn2view0  
   - `caption_tag_dropout_rate`: 0.10（一般用途の起点）。コミュニティガイドで0.1推奨、スタイルは0.2〜0.25もあり得るとされる。citeturn23search11  
   - `caption_dropout_rate`: 0.00〜0.05（既定は0、必要時のみ小さく）。caption dropoutは“やりすぎるとプロンプト無しLoRA”になり得るため。citeturn24view1turn24view0  
3) マルチ概念で枚数差が大きい場合のみ、**フォルダ（概念）単位の倍率調整**を提示（自動適用は慎重に）。

### 閾値と推奨パラメータ（実務的デフォルト案）

以下は「データサイズ未指定（制約なし）」前提の、一般的に事故率が低い目安。

**データ規模トリガ**
- N < 30：**原則バランスしない**（repeats／重み付けはOFF）。データが小さすぎると、バランス操作が“分布作成”になってしまい、過学習的な歪みが出やすい。citeturn22view1turn8view0  
- 30 ≤ N < 200：弱いランダム化＋必要なら“軽い概念倍率”（最大でも×2程度）。  
- N ≥ 200：マルチ概念で枚数差が大きいなら層化・倍率調整を積極的に検討。

**概念（フォルダ／カテゴリ）バランスのデフォルト**
- 各概念iの枚数 n_i に対し、目標露出 T を **median(n_i)** に設定。  
- multiplier m_i = clip(T / n_i, 0.5, 3.0) を基本（EveryDreamがバランス目的なら 1〜3 を推奨）。citeturn8view0  
- m_i < 1 の場合は「各epochで割合抽出（部分サンプリング）」にする（Discussion案と整合）。citeturn11view2turn8view0  
- m_i > 1 の場合は「整数回repeat＋余りはランダム選択」（EveryDreamの説明と整合）。citeturn8view0  

**per-tag cap（タグ上限制御）のデフォルト**
- 自動capは“危険なタグ”に限定：  
  - freq(tag)/N ≥ 0.9 の超頻出タグ（例：常套品質タグ）だけ対象にし、cap_ratio=0.8 を提案（=一部のキャプションから落とす）。  
  - トリガ語（識別子）・クラス語は keep_tokens 相当で保護。citeturn25view0turn6view1turn20view1  
- 長尾タグ（freq≤2）は原則そのまま（過度に増やすと“そのタグが世界の中心”になりうるため）。citeturn8view0turn22view1  

**caption/tag dropoutのデフォルト**
- `caption_tag_dropout_rate`: 0.10（スタイルLoRAなら 0.20 までUIで提案、0.25超は“上級者”扱い）。citeturn23search11  
- `caption_dropout_rate`: 0.00（既定）。ONにする場合の提案値は 0.02〜0.05。0.1以上は注意喚起（wikiが「使いすぎ注意」としている）。citeturn24view1turn24view0  

**正則化（DreamBooth／クラス画像）を使う場合のデフォルト**
- クラス画像を使うなら、まずは公式実装の `prior_loss_weight=1.0` を基準にする。citeturn1view1  
- repeats調整を使う場合は、sd-scriptsの目安式（学習側repeat×枚数≧正則化側repeat×枚数）に沿って「概念が強すぎる」兆候が出たら正則化側露出を少し上げる。citeturn6view1  
- 生成クラス画像を使う場合は、品質が低いと悪影響が出る点を警告する（公式・コミュニティ双方が品質影響を示唆）。citeturn6view1turn8view0turn1view1  

### バランスしない方がよい条件と安全フォールバック

**バランスしない方がよい（デフォルトOFF推奨）**
- データが小さい（例：N<30）かつ目的が“人物一致”など高忠実で、データ分布をいじると学習対象が揺らぐ場合。citeturn22view1turn8view0  
- タグが信頼できない（誤タグが多い／自動タグが不安定）場合：capや層化が誤学習の増幅になる。  
- 長尾意味（レア属性）を“そのまま残したい”場合：均等化は分布を変えるので、むしろ元頻度が情報になっている。citeturn22view1  

**安全フォールバック（Tagmetryの自動復帰案）**
- 学習中サンプルで「クラス語だけで特定個体が出る」兆候が出たら：repeats/重み付けを下げ、正則化（クラス画像）を増やす提案を出す（language drift対策）。citeturn6view1turn22view1turn1view1  
- 「短いプロンプトで出ない」→ caption dropout を少量（0.02〜0.05）追加。ただし0.1以上は“プロンプト無しLoRA化”警告。citeturn24view1turn24view0turn20view2  
- 「滲み（スタイルが他プロンプトに漏れる）」→ preservation混合（ground truth）や、倍率を1に戻す（EveryDreamの記述に沿う）。citeturn8view0  

### 意思決定フローチャート（mermaid）

```mermaid
flowchart TD
  A[入力: 画像+キャプション/タグ] --> B{データ枚数Nは?}
  B -->|N < 30| C[原則バランスしない\n(弱いランダム化のみ)]
  B -->|30 ≤ N < 200| D{マルチ概念?}
  B -->|N ≥ 200| D

  D -->|いいえ| E[shuffle_caption ON\nkeep_tokens=1\ntag_dropout=0.10\ncaption_dropout=0~0.05]
  D -->|はい| F{概念間の枚数比 > 2?}
  F -->|いいえ| E
  F -->|はい| G[概念(フォルダ)単位で倍率調整\nm=clip(median(n)/n_i, 0.5, 3.0)]
  G --> H{タグ品質は良い?}
  H -->|いいえ| I[タグcapはOFF\n倍率+弱いランダム化で様子見]
  H -->|はい| J[超頻出タグのみcap\nfreq/N>=0.9を対象]
  J --> K{DreamBooth/正則化あり?}
  I --> K
  K -->|はい| L[クラス画像/正則化比率も調整\n(drift兆候なら増やす)]
  K -->|いいえ| M[学習中サンプルで監視\n(滲み/ドリフト/過学習)]
```

## 検証方法と評価指標

バランシングは“良くも悪くも分布を変える操作”なので、**小さく回して検証する**のが必須である。ここでは、拡散LoRAで簡単に回せるテストと指標を提示する。

### 最低限の実験デザイン

- **固定プロンプトセット**（20〜50本）を作り、各チェックポイントで同一seedで生成し比較する（sd-scriptsも学習中サンプル出力を支援する）。citeturn15view2turn6view1  
- データを **train/holdout** に分け、holdoutから“代表画像”を10〜30枚抜いておく（後述の過学習検知に使う）。

### 指標

**per-tag recall（タグ再現率）**  
タグtを含むプロンプトで生成した画像を、タグ推定器（例：WD14 tagger）で自動タグ付けし、tが出現する割合を測る。WD14 taggerは「ratings/characters/general tagsをサポート」として公開されている。citeturn21search0turn21search4  
- 目的：バランシングが「特定タグを出しやすくする」効果を持つか確認。  
- 注意：タグ推定器は誤差があるため、絶対値より“設定間差分”を見る。

**FID（Fréchet Inception Distance）の変化**  
生成画像集合と参照画像集合の距離としてFIDが提案されている。citeturn12search0turn12search16  
ただしLoRAの個別概念学習では、参照集合の作り方次第で解釈が難しい（例：人物LoRAで顔一致が重要なのに、FIDは背景や画風で動く）。そのため **補助指標** として使うのが安全。

**CLIP類似度（prompt adherenceの近似）**  
CLIPは画像とテキストの対応を学習したモデルとして公開されており、CLIPScoreはCLIPを用いた参照なし評価として提案されている。citeturn21search6turn21search2  
- 目的：バランシング後に「プロンプトに沿う度合い」が落ちていないかを見る。  
- 注意：CLIPは偏りもあるため、これも差分評価が妥当。

**過学習（memorization）兆候**  
- nearest-neighbor 検知：生成画像と訓練画像の特徴距離（例：DINO特徴）で最も近い訓練画像を探し、類似度が過度に高いものが増えていないかを見る。DINO（自己教師ViT）特徴は転移性が高いことが報告されている。citeturn21search3  
- drift検知：`class` だけのプロンプト（例：`1girl`）で生成し、特定個体が出る比率が上がっていないかを見る（sd-scriptsがこの現象を説明している）。citeturn6view1turn22view1  

### 簡単にできる“ユーザ向けテスト”

- **テストA（短プロンプト耐性）**：トリガ語＋3語程度で安定して特徴が出るか。caption dropout/tag dropoutを上げるとここが改善することがあるが、上げすぎるとプロンプト無し化するので要注意。citeturn24view1turn20view2  
- **テストB（長プロンプト制御性）**：属性語（服・髪色・背景）を変えたときにちゃんと変わるか。shuffle caption と keep_tokens は「先頭語依存」を減らす狙いが説明されている。citeturn25view0turn25view1  
- **テストC（滲み）**：無関係プロンプトでスタイルや顔が漏れ出すか。EveryDreamが挙げる“bleeding”兆候に相当。citeturn8view0  

## 非専門家向けの説明文とFAQ

### 各手法の「なぜ効くの？」（平易版）

**オーバーサンプリング**  
少ない例しかない概念を、授業で何回も復習するイメージ。覚えやすくなるが、同じ問題ばかりで“丸暗記”になりやすい。

**アンダーサンプリング**  
例が多すぎる章は全部やらず、毎回“抜き取りテスト”だけするイメージ。時間は節約できるが、抜き取りが偏ると大事な例を見落とす。

**per-tag cap**  
口癖みたいに毎回出てくる単語（例：品質タグ）を、わざと一部の文章から消して練習するイメージ。特定の単語がないと出せない状態を減らす。

**層化（stratified）サンプリング**  
「人物」「背景」「ポーズ」みたいに棚を分けて、毎回バランスよく取り出すイメージ。何を均等にしたいかが決まっていると強い。

**per-image weight**  
重要な例題に“配点”を付けるイメージ。問題数は変えずに、重要な例の影響を強められる。ただし配点を極端にすると全体が崩れる。

**カリキュラム（段階的）サンプリング**  
最初は簡単な問題（短いキャプション／代表的画像）から始めて、慣れたら難しい条件（長いタグ列／細かい属性）へ進むイメージ。

**ハイブリッド＋Aug**  
同じ写真でも、言い方（キャプション）や少しの見え方（反転・色ずらし）を変えて練習するイメージ。データを増やさずに“見せ方の種類”を増やせる。

### FAQ

**Q: バランスすると必ず良くなりますか？**  
A: ならない。特に拡散LoRAは少数データを叩きやすく、強い均等化は“滲み”や“drift”を誘発し得る。最初は shuffle / dropout のような弱いランダム化から始め、必要条件（マルチ概念で枚数差が極端など）を満たすときだけ倍率調整を使うのが安全。citeturn8view0turn24view1turn25view0

**Q: caption dropoutを上げると何が起きる？**  
A: 短いプロンプトで出しやすくなることがある一方、上げすぎると“プロンプト無しLoRA”に寄って制御性が落ちる、と明示的に注意されている。citeturn24view1turn20view2

**Q: repeatsを上げれば上げるほど良い？**  
A: 良くない。repeatsは概念の露出を増やすが、その分“記憶”や“drift”を起こしやすいので、正則化画像／preservation混合や早期停止で抑えるのが基本。citeturn6view1turn22view1turn8view0

**Q: 正則化画像（クラス画像）は生成画像でいい？**  
A: 公式実装ではモデル生成のクラス画像を用いた prior preservation loss が案内されている一方、コミュニティ実装では「生成画像はモデルの誤りを強化し得る」との懸念もある。品質が低い場合は、別ソースの“ground truth”を混ぜる案も検討余地がある。citeturn1view1turn8view0turn6view1