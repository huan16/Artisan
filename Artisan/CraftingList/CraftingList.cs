﻿using Artisan.Autocraft;
using Artisan.GameInterop;
using Artisan.RawInformation;
using Artisan.RawInformation.Character;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using ECommons;
using ECommons.Automation;
using ECommons.Automation.LegacyTaskManager;
using ECommons.Automation.UIInput;
using ECommons.DalamudServices;
using ECommons.ExcelServices;
using ECommons.Logging;
using ECommons.UIHelpers.AddonMasterImplementations;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using System;
using System.Collections.Generic;
using System.Linq;
using static ECommons.GenericHelpers;

namespace Artisan.CraftingLists
{
    public class CraftingList
    {
        public int ID { get; set; }

        public string? Name { get; set; }

        public List<uint> Items { get; set; } = new();

        public Dictionary<uint, ListItemOptions> ListItemOptions { get; set; } = new();

        public bool SkipIfEnough { get; set; }

        public bool SkipLiteral = false;

        public bool Materia { get; set; }

        public bool Repair { get; set; }

        public int RepairPercent = 50;

        public bool AddAsQuickSynth;
    }

    public class NewCraftingList
    {
        public int ID { get; set; }

        public string? Name { get; set; }

        // 缓存每个物品的总需求数量
        public readonly Dictionary<uint, int> ResultItemCache = new();

        public List<ListItem> Recipes { get; set; } = new();

        public List<uint> ExpandedList { get; set; } = new();

        public bool SkipIfEnough { get; set; }

        public bool SkipLiteral = false;

        public bool Materia { get; set; }

        public bool Repair { get; set; }

        public int RepairPercent = 50;

        public bool AddAsQuickSynth;
    }

    public class ListItem
    {
        public uint ID { get; set; }

        public int Quantity { get; set; }

        public ListItemOptions? ListItemOptions { get; set; } = new();

    }

    public class ListItemOptions
    {
        public bool NQOnly { get; set; }
        // TODO: custom RecipeConfig?

        public bool Skipping { get; set; }
    }

    public static class CraftingListFunctions
    {
        public static int CurrentIndex;

        public static bool Paused { get; set; } = false;

        public static Dictionary<uint, int>? Materials;

        public static TaskManager CLTM = new();

        public static TimeSpan ListEndTime = default(TimeSpan);

        public static void SetID(this NewCraftingList list)
        {
            var rng = new Random();
            var proposedRNG = rng.Next(1, 50000);
            while (P.Config.NewCraftingLists.Where(x => x.ID == proposedRNG).Any())
            {
                proposedRNG = rng.Next(1, 50000);
            }

            list.ID = proposedRNG;
        }

        public static Dictionary<uint, int> ListMaterials(this NewCraftingList list)
        {
            var output = new Dictionary<uint, int>();
            foreach (var item in list.Recipes)
            {
                if (item.ListItemOptions == null)
                {
                    item.ListItemOptions = new ListItemOptions();
                    P.Config.Save();
                }
                if (item.ListItemOptions.Skipping || item.Quantity == 0) continue;
                Recipe r = LuminaSheets.RecipeSheet[item.ID];
                CraftingListHelpers.AddRecipeIngredientsToList(r, ref output, false, list);
            }

            return output;
        }

        public static bool Save(this NewCraftingList list, bool isNew = false)
        {
            if (list.Recipes.Count == 0 && !isNew) return false;

            list.SkipIfEnough = P.Config.DefaultListSkip;
            list.Materia = P.Config.DefaultListMateria;
            list.Repair = P.Config.DefaultListRepair;
            list.RepairPercent = P.Config.DefaultListRepairPercent;
            list.AddAsQuickSynth = P.Config.DefaultListQuickSynth;

            if (list.AddAsQuickSynth)
            {
                foreach (var item in list.Recipes)
                {
                    item.ListItemOptions ??= new ListItemOptions();
                    item.ListItemOptions.NQOnly = true;
                }
            }

            P.Config.NewCraftingLists.Add(list);
            P.Config.Save();
            return true;
        }

        /// <summary>
        /// 检测配方窗口是否已打开且有效
        /// </summary>
        public static unsafe bool RecipeWindowOpen()
        {
            return TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon) 
                && addon->AtkUnitBase.IsVisible 
                && Operations.GetSelectedRecipeEntry() != null;
        }

        /// <summary>
        /// 通过ID打开指定配方（包含状态检查与防抖机制）
        /// </summary>
        /// <param name="skipThrottle">是否跳过操作间隔限制</param>
        public static unsafe void OpenRecipeByID(uint recipeID, bool skipThrottle = false)
        {
            if (Crafting.CurState != Crafting.State.IdleNormal) return;
            
            var currentRecipe = Operations.GetSelectedRecipeEntry();
            if (currentRecipe != null && currentRecipe->RecipeId == recipeID) return;

            AgentRecipeNote.Instance()->OpenRecipeByRecipeId(recipeID);
        }

        /// <summary>
        /// 验证是否拥有配方所需全部材料
        /// </summary>
        public static bool HasItemsForRecipe(uint recipeId)
        {
            if (recipeId == 0) return false;
            var recipe = LuminaSheets.RecipeSheet[recipeId];
            return recipe.RowId != 0 && CraftingListUI.CheckForIngredients(recipe, false);
        }

        internal static unsafe void ProcessList(NewCraftingList selectedList)
        {
            var isCrafting = Svc.Condition[ConditionFlag.Crafting];
            var preparing = Svc.Condition[ConditionFlag.PreparingToCraft];
            Materials ??= selectedList.ListMaterials();

            if (Paused)
            {
                return;
            }

            if (CurrentIndex < selectedList.ExpandedList.Count)
            {
                if (CraftingListUI.CurrentProcessedItem != selectedList.ExpandedList[CurrentIndex])
                {
                    CraftingListUI.CurrentProcessedItem = selectedList.ExpandedList[CurrentIndex];
                    CraftingListUI.CurrentProcessedItemCount = 1;
                    CraftingListUI.CurrentProcessedItemIndex = CurrentIndex;
                    CraftingListUI.CurrentProcessedItemListCount = selectedList.ExpandedList.Count(v => v == CraftingListUI.CurrentProcessedItem);

                }
                else if (CraftingListUI.CurrentProcessedItemIndex != CurrentIndex)
                {
                    CraftingListUI.CurrentProcessedItemIndex = CurrentIndex;
                    CraftingListUI.CurrentProcessedItemCount++;
                }
            }
            else
            {
                Svc.Log.Verbose("End of Index");
                CurrentIndex = 0;
                CraftingListUI.Processing = false;
                Operations.CloseQuickSynthWindow();
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromSeconds(5)));

                if (P.Config.PlaySoundFinishList)
                    Sounds.SoundPlayer.PlaySound();
                return;
            }

            var recipe = LuminaSheets.RecipeSheet[CraftingListUI.CurrentProcessedItem];
            var options = selectedList.Recipes.First(x => x.ID == CraftingListUI.CurrentProcessedItem).ListItemOptions;
            var config = /* options?.CustomConfig ?? */ P.Config.RecipeConfigs.GetValueOrDefault(CraftingListUI.CurrentProcessedItem) ?? new();
            var needToRepair = selectedList.Repair && RepairManager.GetMinEquippedPercent() < selectedList.RepairPercent && (RepairManager.CanRepairAny() || RepairManager.RepairNPCNearby(out _));
            PreCrafting.CraftType type = (options?.NQOnly ?? false) && recipe.CanQuickSynth && P.ri.HasRecipeCrafted(recipe.RowId) ? PreCrafting.CraftType.Quick : PreCrafting.CraftType.Normal;

            if (Crafting.QuickSynthState.Max > 0 && (needToRepair || Crafting.QuickSynthCompleted || selectedList.Materia && Spiritbond.IsSpiritbondReadyAny() && CharacterInfo.MateriaExtractionUnlocked()))
            {
                Operations.CloseQuickSynthWindow();
            }

            if (PreCrafting.Tasks.Count > 0 || Crafting.CurState is not Crafting.State.IdleNormal and not Crafting.State.IdleBetween and not Crafting.State.InvalidState)
            {
                return;
            }

            if (recipe.SecretRecipeBook.RowId != 0)
            {
                if (!PlayerState.Instance()->IsSecretRecipeBookUnlocked(recipe.SecretRecipeBook.RowId))
                {
                    SeString error = new SeString(
                        new TextPayload("You haven't unlocked the recipe book "),
                        new ItemPayload(recipe.SecretRecipeBook.Value.Item.RowId),
                        new UIForegroundPayload(1),
                        new TextPayload(recipe.SecretRecipeBook.Value.Name.ToString()),
                        RawPayload.LinkTerminator,
                        UIForegroundPayload.UIForegroundOff,
                        new TextPayload(" for this recipe. Moving on."));

                    Svc.Chat.Print(new Dalamud.Game.Text.XivChatEntry()
                    {
                        Message = error,
                        Type = Dalamud.Game.Text.XivChatType.ErrorMessage,
                    });

                    var currentRecipe = selectedList.ExpandedList[CurrentIndex];
                    while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                    {
                        ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                        CurrentIndex++;
                        if (CurrentIndex == selectedList.ExpandedList.Count)
                            return;
                    }
                }
            }

            if (selectedList.SkipIfEnough && (preparing || !isCrafting))
            {
                var ItemId = recipe.ItemResult.RowId;
                int numMats = Materials.Any(x => x.Key == recipe.ItemResult.RowId) && !selectedList.SkipLiteral ? Materials.First(x => x.Key == recipe.ItemResult.RowId).Value : selectedList.ExpandedList.Count(x => LuminaSheets.RecipeSheet[x].ItemResult.RowId == ItemId) * recipe.AmountResult;
                if (numMats <= CraftingListUI.NumberOfIngredient(recipe.ItemResult.RowId))
                {
                    DuoLog.Error($"Skipping {recipe.ItemResult.Value.Name.ToDalamudString()} due to having enough in inventory [Skip Items you already have enough of]");

                    var currentRecipe = selectedList.ExpandedList[CurrentIndex];
                    while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                    {
                        ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                        CurrentIndex++;
                        if (CurrentIndex == selectedList.ExpandedList.Count)
                            return;
                    }

                    return;
                }
            }

            if (!HasItemsForRecipe(CraftingListUI.CurrentProcessedItem) && (preparing || !isCrafting))
            {
                DuoLog.Error($"Insufficient materials for {recipe.ItemResult.Value.Name.ToDalamudString().ExtractText()}. Moving on.");
                var currentRecipe = selectedList.ExpandedList[CurrentIndex];

                while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                {
                    ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                    CurrentIndex++;
                    if (CurrentIndex == selectedList.ExpandedList.Count)
                        return;
                }

                return;
            }

            if (Svc.ClientState.LocalPlayer.ClassJob.RowId != recipe.CraftType.Value.RowId + 8)
            {
                PreCrafting.equipGearsetLoops = 0;
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                PreCrafting.Tasks.Add((() => PreCrafting.TaskClassChange((Job)recipe.CraftType.Value.RowId + 8), TimeSpan.FromMilliseconds(200)));

                return;
            }

            bool needEquipItem = recipe.ItemRequired.RowId > 0 && !PreCrafting.IsItemEquipped(recipe.ItemRequired.RowId);
            if (needEquipItem)
            {
                PreCrafting.equipAttemptLoops = 0;
                PreCrafting.Tasks.Add((() => PreCrafting.TaskEquipItem(recipe.ItemRequired.RowId), TimeSpan.FromMilliseconds(200)));
                return;
            }

            if (Svc.ClientState.LocalPlayer.Level < recipe.RecipeLevelTable.Value.ClassJobLevel - 5 && Svc.ClientState.LocalPlayer.ClassJob.RowId == recipe.CraftType.Value.RowId + 8 && !isCrafting && !preparing)
            {
                DuoLog.Error("Insufficient level to craft this item. Moving on.");
                var currentRecipe = selectedList.ExpandedList[CurrentIndex];

                while (currentRecipe == selectedList.ExpandedList[CurrentIndex])
                {
                    ListEndTime = ListEndTime.Subtract(CraftingListUI.GetCraftDuration(currentRecipe, type == PreCrafting.CraftType.Quick)).Subtract(TimeSpan.FromSeconds(1));
                    CurrentIndex++;
                    if (CurrentIndex == selectedList.ExpandedList.Count)
                        return;
                }

                return;
            }

            if (!Spiritbond.ExtractMateriaTask(selectedList.Materia))
            {
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                return;
            }

            if (selectedList.Repair && !RepairManager.ProcessRepair(selectedList))
            {
                PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200)));
                return;
            }

            if (selectedList.Recipes.First(x => x.ID == CraftingListUI.CurrentProcessedItem).ListItemOptions is null)
            {
                selectedList.Recipes.First(x => x.ID == CraftingListUI.CurrentProcessedItem).ListItemOptions = new ListItemOptions();
            }
            bool needConsumables = PreCrafting.NeedsConsumablesCheck(type, config);
            bool hasConsumables = PreCrafting.HasConsumablesCheck(config);

            if (P.Config.AbortIfNoFoodPot && needConsumables && !hasConsumables)
            {
                PreCrafting.MissingConsumablesMessage(recipe, config);
                Paused = false;
                return;
            }

            bool needFood = config != default && ConsumableChecker.HasItem(config.RequiredFood, config.RequiredFoodHQ) && !ConsumableChecker.IsFooded(config);
            bool needPot = config != default && ConsumableChecker.HasItem(config.RequiredPotion, config.RequiredPotionHQ) && !ConsumableChecker.IsPotted(config);
            bool needManual = config != default && ConsumableChecker.HasItem(config.RequiredManual, false) && !ConsumableChecker.IsManualled(config);
            bool needSquadronManual = config != default && ConsumableChecker.HasItem(config.RequiredSquadronManual, false) && !ConsumableChecker.IsSquadronManualled(config);

            if (needFood || needPot || needManual || needSquadronManual)
            {
                if (!CLTM.IsBusy && !PreCrafting.Occupied())
                {
                    CLTM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskExitCraft(), TimeSpan.FromMilliseconds(200))));
                    CLTM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskUseConsumables(config, type), TimeSpan.FromMilliseconds(200))));
                    CLTM.DelayNext(100);
                }
                return;
            }

            if (Crafting.CurState is Crafting.State.IdleBetween or Crafting.State.IdleNormal && !PreCrafting.Occupied())
            {
                if (!CLTM.IsBusy)
                {
                    CLTM.Enqueue(() => PreCrafting.Tasks.Add((() => PreCrafting.TaskSelectRecipe(recipe), TimeSpan.FromMilliseconds(200))));

                    if (!RecipeWindowOpen()) return;

                    if (type == PreCrafting.CraftType.Quick)
                    {
                        var lastIndex = selectedList.ExpandedList.LastIndexOf(CraftingListUI.CurrentProcessedItem);
                        var count = lastIndex - CurrentIndex + 1;
                        count = GetRecipeExpectedCraftNumber(selectedList, recipe, count);
                        if (count >= 99)
                        {
                            CLTM.Enqueue(() => Operations.QuickSynthItem(99));
                            CLTM.Enqueue(() => Crafting.CurState is Crafting.State.InProgress or Crafting.State.QuickCraft, 2000, "ListQS99WaitStart");
                            return;
                        }
                        else
                        {
                            CLTM.Enqueue(() => Operations.QuickSynthItem(count));
                            CLTM.Enqueue(() => Crafting.CurState is Crafting.State.InProgress or Crafting.State.QuickCraft, 2000, "ListQSCountWaitStart");
                            return;
                        }
                    }
                    else if (type == PreCrafting.CraftType.Normal)
                    {
                        CLTM.DelayNext((int)(Math.Min(P.Config.ListCraftThrottle2, 2) * 1000));
                        CLTM.Enqueue(() => SetIngredients(), "SettingIngredients");
                        CLTM.Enqueue(() => Operations.RepeatActualCraft(), "ListCraft");
                        CLTM.Enqueue(() => Crafting.CurState is Crafting.State.InProgress or Crafting.State.QuickCraft, 2000, "ListNormalWaitStart");
                        return;

                    }
                }
            }
        }

        /// <summary>
        /// 计算需要制作的物品数量，考虑跳过足够物品的情况
        /// </summary>
        private static int GetRecipeExpectedCraftNumber(NewCraftingList selectedList, Recipe recipe, int initialCount)
        {
            if (!selectedList.SkipIfEnough) return initialCount;

            // 预计算关键数据
            var resultItemId = recipe.ItemResult.RowId;
            var resultPerCraft = recipe.AmountResult;
            var inventoryCount = CraftingListUI.NumberOfIngredient(resultItemId);

            // 计算总需求数量
            var totalRequired = CalculateTotalRequired(selectedList, resultItemId, resultPerCraft);

            // 计算实际需要制作的数量
            var actualToCraft = CalculateActualCraftCount(selectedList, recipe, inventoryCount, totalRequired);

            // 计算最终制作次数（考虑每次制作产出数量）
            return CalculateFinalCraftCount(actualToCraft, resultPerCraft);
        }

        /// <summary>
        /// 计算该物品在清单中的总需求数量
        /// </summary>
        private static int CalculateTotalRequired(NewCraftingList list, uint itemId, int resultPerCraft)
        {
            // 使用字典缓存计算结果
            if (!list.ResultItemCache.TryGetValue(itemId, out var total))
            {
                total = list.ExpandedList
                    .Where(recipeId => LuminaSheets.RecipeSheet[recipeId].ItemResult.RowId == itemId)
                    .Sum(_ => resultPerCraft);

                list.ResultItemCache[itemId] = total;
            }
            return total;
        }

        /// <summary>
        /// 计算实际需要制作的数量（考虑库存和跳过逻辑）
        /// </summary>
        private static int CalculateActualCraftCount(NewCraftingList list, Recipe recipe, int inventoryCount, int totalRequired)
        {
            var resultItemId = recipe.ItemResult.RowId;
            var shouldUseMaterialLogic = !list.SkipLiteral && Materials!.Any(x => x.Key == resultItemId);

            if (shouldUseMaterialLogic)
            {
                return Materials!.First(x => x.Key == resultItemId).Value;
            }

            // 计算剩余需要制作的数量（当前索引之后的部分）
            var remainingCrafts = list.ExpandedList
                .Where((recipeId, index) =>
                    index >= CurrentIndex &&
                    LuminaSheets.RecipeSheet[recipeId].ItemResult.RowId == resultItemId)
                .Sum(_ => recipe.AmountResult);

            var deficit = Math.Max(totalRequired - inventoryCount, 0);
            return Math.Min(remainingCrafts, deficit);
        }

        /// <summary>
        /// 计算最终需要执行制作的次数
        /// </summary>
        private static int CalculateFinalCraftCount(int requiredItems, int resultPerCraft)
        {
            var (quotient, remainder) = Math.DivRem(requiredItems, resultPerCraft);
            return quotient + (remainder > 0 ? 1 : 0);
        }

        public static unsafe bool SetIngredients(EnduranceIngredients[]? setIngredients = null)
        {
            var recipe = Operations.GetSelectedRecipeEntry();
            if (recipe == null)
                return false;

            if (TryGetAddonByName<AddonRecipeNote>("RecipeNote", out var addon) &&
                addon->AtkUnitBase.IsVisible &&
                AgentRecipeNote.Instance() != null &&
                RaptureAtkModule.Instance()->AtkModule.IsAddonReady(AgentRecipeNote.Instance()->AgentInterface.AddonId))
            {
                if (setIngredients == null || Endurance.IPCOverride)
                {
                    for (int i = 0; i <= 5; i++)
                    {
                        try
                        {
                            var node = addon->AtkUnitBase.UldManager.NodeList[23 - i]->GetAsAtkComponentNode();

                            if (node is null || !node->AtkResNode.IsVisible())
                            {
                                continue;
                            }

                            if (node->Component->UldManager.NodeList[11]->IsVisible())
                            {
                                var ingredient = LuminaSheets.RecipeSheet.Values.Where(x => x.RowId == Endurance.RecipeID).FirstOrDefault().Ingredients().ElementAt(i).Item;

                                var btn = node->Component->UldManager.NodeList[14]->GetAsAtkComponentButton();
                                try
                                {
                                    btn->ClickAddonButton((AtkComponentBase*)addon, 4, EventType.CHANGE);
                                }
                                catch (Exception ex)
                                {
                                    ex.Log();
                                }
                                var contextMenu = (AtkUnitBase*)Svc.GameGui.GetAddonByName("ContextIconMenu");
                                if (contextMenu != null)
                                {
                                    Callback.Fire(contextMenu, true, 0, 0, 0, ingredient, 0);
                                }
                            }
                            else
                            {
                                for (int m = 0; m <= 100; m++)
                                {
                                    new AddonMaster.RecipeNote((IntPtr)addon).Material((uint)i, false);
                                }

                                for (int m = 0; m <= 100; m++)
                                {
                                    new AddonMaster.RecipeNote((IntPtr)addon).Material((uint)i, true);
                                }
                            }

                        }
                        catch
                        {
                            return false;
                        }
                    }
                }
                else
                {
                    for (uint i = 0; i <= 5; i++)
                    {
                        try
                        {
                            var node = addon->AtkUnitBase.UldManager.NodeList[23 - i]->GetAsAtkComponentNode();
                            if (node->Component->UldManager.NodeListCount < 16)
                                return false;

                            if (node is null || !node->AtkResNode.IsVisible())
                            {
                                continue;
                            }

                            var hqSetButton = node->Component->UldManager.NodeList[6]->GetAsAtkComponentNode();
                            var nqSetButton = node->Component->UldManager.NodeList[9]->GetAsAtkComponentNode();

                            var hqSetText = hqSetButton->Component->UldManager.NodeList[2]->GetAsAtkTextNode()->NodeText;
                            var nqSetText = nqSetButton->Component->UldManager.NodeList[2]->GetAsAtkTextNode()->NodeText;

                            int hqSet = Convert.ToInt32(hqSetText.ToString().GetNumbers());
                            int nqSet = Convert.ToInt32(nqSetText.ToString().GetNumbers());

                            if (setIngredients.Any(y => y.IngredientSlot == i))
                            {
                                for (int h = hqSet; h < setIngredients.First(x => x.IngredientSlot == i).HQSet; h++)
                                {
                                    new AddonMaster.RecipeNote((IntPtr)addon).Material(i, true);
                                }

                                for (int h = nqSet; h < setIngredients.First(x => x.IngredientSlot == i).NQSet; h++)
                                {
                                    new AddonMaster.RecipeNote((IntPtr)addon).Material(i, false);
                                }
                            }
                        }
                        catch
                        {
                            return false;
                        }
                    }
                }
            }
            else
            {
                return false;
            }

            return true;
        }
    }
}
