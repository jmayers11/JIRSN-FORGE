-- Persist configurable work-center positions on the supervisor's visual plant floor map.
ALTER TABLE resources.work_center
    ADD COLUMN layout_x integer NULL CHECK (layout_x BETWEEN 0 AND 3),
    ADD COLUMN layout_y integer NULL CHECK (layout_y BETWEEN 0 AND 99),
    ADD CONSTRAINT work_center_layout_pair CHECK ((layout_x IS NULL) = (layout_y IS NULL));
