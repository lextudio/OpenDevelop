#!/bin/bash
BASE="/Users/lextm/wpf-tools/OpenDevelop/src/Main/ICSharpCode.Core.Presentation/Resources/VS2017"

create_icon() {
  local dir="$BASE/$1"
  local file="$dir/${1}_16x.xaml"
  mkdir -p "$dir"
  cat > "$file" << 'XMLEOF'
<?xml version="1.0" encoding="utf-8"?>
<Viewbox Width="16" Height="16" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
  <Rectangle Width="16" Height="16">
    <Rectangle.Fill>
      <DrawingBrush>
        <DrawingBrush.Drawing>
          <DrawingGroup>
            <DrawingGroup.Children>
              <GeometryDrawing Brush="#00FFFFFF" Geometry="F1M16,16L0,16 0,0 16,0z" />
              <GeometryDrawing Brush="#FF00539C" Geometry="GEOPLACEHOLDER" />
            </DrawingGroup.Children>
          </DrawingGroup>
        </DrawingBrush.Drawing>
      </DrawingBrush>
    </Rectangle.Fill>
  </Rectangle>
</Viewbox>
XMLEOF
  sed -i '' "s|GEOPLACEHOLDER|$2|g" "$file"
}

# Alignment icons - vertical reference line with horizontal bars/arrows

# AlignLefts: vertical line on left, horizontal bars extending right
create_icon "AlignLefts" "F1M3,2L3,14 M3,4L10,4 M3,8L12,8 M3,12L9,12 M10,4L8,3 8,5z M12,8L10,7 10,9z M9,12L7,11 7,13z"

# AlignCenters: vertical center line, horizontal bars symmetric
create_icon "AlignCenters" "F1M8,2L8,14 M5,4L11,4 M4,8L12,8 M5,12L11,12 M5,4L6,3 6,5z M11,4L10,3 10,5z M4,8L5,7 5,9z M12,8L11,7 11,9z M5,12L6,11 6,13z M11,12L10,11 10,13z"

# AlignRights: vertical line on right, horizontal bars extending left
create_icon "AlignRights" "F1M13,2L13,14 M6,4L13,4 M4,8L13,8 M7,12L13,12 M6,4L8,3 8,5z M4,8L6,7 6,9z M7,12L9,11 9,13z"

# AlignTops: horizontal line on top, vertical bars extending down
create_icon "AlignTops" "F1M2,3L14,3 M4,3L4,10 M8,3L8,12 M12,3L12,9 M4,10L3,8 5,8z M8,12L7,10 9,10z M12,9L11,7 13,7z"

# AlignMiddles: horizontal center line, vertical bars symmetric
create_icon "AlignMiddles" "F1M2,8L14,8 M4,5L4,11 M8,4L8,12 M12,5L12,11 M4,5L3,6 5,6z M4,11L3,10 5,10z M8,4L7,5 9,5z M8,12L7,11 9,11z M12,5L11,6 13,6z M12,11L11,10 13,10z"

# AlignBottoms: horizontal line on bottom, vertical bars extending up
create_icon "AlignBottoms" "F1M2,13L14,13 M4,6L4,13 M8,4L8,13 M12,7L12,13 M4,6L3,8 5,8z M8,4L7,6 9,6z M12,7L11,9 13,9z"

# AlignToGrid: grid pattern
create_icon "AlignToGrid" "F1M2,2L2,14 14,14 14,2z M2,6L14,6 M2,10L14,10 M6,2L6,14 M10,2L10,14"

# MakeSameWidth: two rectangles with horizontal arrows
create_icon "MakeSameWidth" "F1M3,3L3,13 7,13 7,3z M9,5L9,11 13,11 13,5z M7,4L9,4 M7,8L9,8 M7,12L9,12"

# SizeToGrid: rectangle with grid lines
create_icon "SizeToGrid" "F1M3,3L3,13 13,13 13,3z M3,6L13,6 M3,10L13,10 M6,3L6,13 M10,3L10,13"

# MakeSameHeight: two rectangles with vertical arrows
create_icon "MakeSameHeight" "F1M3,3L7,3 7,13 3,13z M9,5L13,5 13,11 9,11z M4,7L4,5 5,4z M8,7L8,5 9,4z M4,9L4,11 5,12z M8,9L8,11 9,12z"

# MakeSameSize: two rectangles with both arrows
create_icon "MakeSameSize" "F1M3,3L7,3 7,7 3,7z M9,9L13,9 13,13 9,13z M7,4L9,4 M7,5L9,5 M4,7L4,9 5,9z M5,7L5,9"

# EqualizeHorizontalSpace: three vertical bars with spacing arrows
create_icon "EqualizeHorizontalSpace" "F1M3,3L3,13 M6,3L6,13 M10,3L10,13 M13,3L13,13 M4,6L5,5 5,7z M11,6L12,5 12,7z M4,10L5,9 5,11z M11,10L12,9 12,11z"

# IncreaseHorizontalSpace: two vertical bars with outward arrows
create_icon "IncreaseHorizontalSpace" "F1M5,3L5,13 M11,3L11,13 M3,6L2,5 2,7z M13,6L14,5 14,7z M3,10L2,9 2,11z M13,10L14,9 14,11z M7,5L9,5 M7,8L9,8 M7,11L9,11"

# DecreaseHorizontalSpace: two vertical bars with inward arrows
create_icon "DecreaseHorizontalSpace" "F1M5,3L5,13 M11,3L11,13 M4,6L5,5 5,7z M12,6L11,5 11,7z M4,10L5,9 5,11z M12,10L11,9 11,11z M7,5L9,5 M7,8L9,8 M7,11L9,11"

# RemoveHorizontalSpace: two vertical bars merging
create_icon "RemoveHorizontalSpace" "F1M5,3L5,13 M11,3L11,13 M4,6L5,5 5,7z M12,6L11,5 11,7z M4,10L5,9 5,11z M12,10L11,9 11,11z M7,8L9,8"

# EqualizeVerticalSpace: three horizontal bars with spacing arrows
create_icon "EqualizeVerticalSpace" "F1M3,3L13,3 M3,6L13,6 M3,10L13,10 M3,13L13,13 M6,4L5,5 7,5z M10,4L9,5 11,5z M6,7L5,8 7,8z M10,7L9,8 11,8z M6,11L5,12 7,12z M10,11L9,12 11,12z"

# IncreaseVerticalSpace: two horizontal bars with outward arrows
create_icon "IncreaseVerticalSpace" "F1M3,5L13,5 M3,11L13,11 M6,3L5,2 7,2z M10,3L9,2 11,2z M6,13L5,14 7,14z M10,13L9,14 11,14z M5,7L5,9 M8,7L8,9 M11,7L11,9"

# DecreaseVerticalSpace: two horizontal bars with inward arrows
create_icon "DecreaseVerticalSpace" "F1M3,5L13,5 M3,11L13,11 M6,4L5,5 7,5z M10,4L9,5 11,5z M6,12L5,11 7,11z M10,12L9,11 11,11z M5,7L5,9 M8,7L8,9 M11,7L11,9"

# RemoveVerticalSpace: two horizontal bars merging
create_icon "RemoveVerticalSpace" "F1M3,5L13,5 M3,11L13,11 M6,4L5,5 7,5z M10,4L9,5 11,5z M6,12L5,11 7,11z M10,12L9,11 11,11z M8,7L8,9"

# CenterHorizontally: horizontal center arrow
create_icon "CenterHorizontally" "F1M8,2L8,14 M2,8L14,8 M4,4L8,4 M12,4L8,4 M4,12L8,12 M12,12L8,12 M4,4L3,3 3,5z M12,4L13,3 13,5z M4,12L3,11 3,13z M12,12L13,11 13,13z"

# CenterVertically: vertical center arrow
create_icon "CenterVertically" "F1M8,2L8,14 M2,8L14,8 M4,4L4,8 M12,4L12,8 M4,12L4,8 M12,12L12,8 M4,4L3,3 5,3z M12,4L11,3 13,3z M4,12L3,11 5,11z M12,12L11,11 13,11z"

# BringToFront: two overlapping rectangles, front one offset up-left
create_icon "BringToFront" "F1M5,5L5,13 13,13 13,5z M3,3L3,11 11,11 11,3z M5,5L5,13 13,13 13,5z M3,3L11,3 M3,3L3,11"

# SendToBack: two overlapping rectangles, back one offset up-left
create_icon "SendToBack" "F1M3,3L3,11 11,11 11,3z M5,5L5,13 13,13 13,5z M3,3L11,3 M3,3L3,11"

# LockControls: padlock shape
create_icon "LockControls" "F1M6,7L6,5C6,2.79 7.79,1 10,1 12.21,1 14,2.79 14,5L14,7 M6,7L14,7 14,15 6,15z M8,7L8,5C8,3.9 8.9,3 10,3 11.1,3 12,3.9 12,5L12,7 M9,10L9,12 M11,10L11,12 M8,11L12,11"

echo "Created 24 icon directories and files."
