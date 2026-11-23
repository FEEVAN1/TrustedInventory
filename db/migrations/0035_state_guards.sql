-- 0035 (M6, M2 carry-over): reservation and excursion state machines enforced in the database (STATE-13, DEC-102).
-- The application already moves these states only along the arrows below; the triggers make every other path (a bug, a script,
-- a future command) fail as well, like the guards on approvals, lots and the ledger.
--   reservation: Active -> Picked | Released | Expired;  Picked -> Active (unpick) | Consumed | Released (fold);
--                Consumed, Released and Expired are final.
--   excursion:   Open -> Acknowledged | Resolved;  Acknowledged -> Resolved;  a Resolved excursion never changes again.
CREATE FUNCTION ti.reservation_state_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.state IS DISTINCT FROM OLD.state AND NOT (
       (OLD.state = 'Active' AND NEW.state IN ('Picked', 'Released', 'Expired'))
    OR (OLD.state = 'Picked' AND NEW.state IN ('Active', 'Consumed', 'Released'))) THEN
    RAISE EXCEPTION 'A reservation cannot move from % to % (section 13)', OLD.state, NEW.state USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER reservation_state_guard BEFORE UPDATE ON ti.reservation FOR EACH ROW EXECUTE FUNCTION ti.reservation_state_guard();

CREATE FUNCTION ti.excursion_status_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF OLD.status = 'Resolved' AND ROW(NEW.*) IS DISTINCT FROM ROW(OLD.*) THEN
    RAISE EXCEPTION 'A resolved excursion is final' USING ERRCODE = 'check_violation';
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NOT (
       (OLD.status = 'Open' AND NEW.status IN ('Acknowledged', 'Resolved'))
    OR (OLD.status = 'Acknowledged' AND NEW.status = 'Resolved')) THEN
    RAISE EXCEPTION 'An excursion cannot move from % to %', OLD.status, NEW.status USING ERRCODE = 'check_violation';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER excursion_status_guard BEFORE UPDATE ON ti.excursion FOR EACH ROW EXECUTE FUNCTION ti.excursion_status_guard();
